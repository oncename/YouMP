Option Strict On
Option Explicit On

Imports System
Imports System.Collections.Generic
Imports System.Diagnostics
Imports System.Threading
Imports System.Threading.Tasks
Imports NAudio.CoreAudioApi
Imports NAudio.Wave
Imports NAudio.Wave.SampleProviders
Imports yoump.IServices

Public Class NAudioSyncPlayer
    Implements IAudioPlayer, IDisposable, IAsyncDisposable

    Public Enum NAudioBackend
        Wasapi
        Asio
    End Enum

    Public Event AudioError As EventHandler(Of String)

    Private ReadOnly _backendType As NAudioBackend
    Private _waveOut As IWavePlayer
    Private _mixer As MixingSampleProvider

    Private _multitrackProvider As NleMultitrackProvider
    Private _mainTrack As DynamicSpeedSampleProvider
    Private _scrubTrack As FastScrubProvider

    Private _volume As Single = 1.0F
    Private _isPlaying As Boolean = False
    Private ReadOnly _audioLock As New Object()
    Private _disposed As Boolean = False

    Private _waveFormat As WaveFormat
    Private _wasapiBufferMs As Integer = 100

    ' Свойство для инъекции ProjectModel из контроллера
    Public Property ProjectModel As ProjectModel
        Get
            Return _multitrackProvider?.Model
        End Get
        Set(value As ProjectModel)
            If _multitrackProvider IsNot Nothing Then
                _multitrackProvider.Model = value
            End If
        End Set
    End Property

    Public Sub New(backend As NAudioBackend)
        _backendType = backend
        InitEngine()
    End Sub

    Private Sub InitEngine()
        Try
            If _backendType = NAudioBackend.Asio Then
                _waveOut = New AsioOut()
            Else
                Try
                    Dim parsedBuffer As Integer = SettingsService.Instance.Current.AudioBufferMs
                    If parsedBuffer >= 10 Then
                        _wasapiBufferMs = parsedBuffer
                    Else
                        Debug.WriteLine("[NAudio] Неверное значение AudioBufferMs. Используется 100 мс.")
                    End If
                Catch
                End Try
                _waveOut = New WasapiOut(AudioClientShareMode.Shared, True, _wasapiBufferMs)
            End If

            _waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)
            _mixer = New MixingSampleProvider(_waveFormat) With {.ReadFully = True}

            ' Инициализируем наш новый NLE-микшер
            _multitrackProvider = New NleMultitrackProvider(48000, 2)

            _mainTrack = New DynamicSpeedSampleProvider(_multitrackProvider) With {.Volume = _volume}
            _mixer.AddMixerInput(_mainTrack)

            _waveOut.Init(_mixer)
            _waveOut.Play()
        Catch ex As Exception
            Debug.WriteLine($"[NAudio] Ошибка инициализации {_backendType}: {ex.Message}")
            _waveOut?.Dispose()
            _waveOut = Nothing
            Throw
        End Try
    End Sub

    Public Property Volume As Integer Implements IAudioPlayer.Volume
        Get
            Return CInt(_volume * 100.0F)
        End Get
        Set(value As Integer)
            _volume = Math.Max(0, value) / 100.0F
            SyncLock _audioLock
                If _mainTrack IsNot Nothing Then _mainTrack.Volume = _volume
                If _scrubTrack IsNot Nothing Then _scrubTrack.Volume = _volume
            End SyncLock
        End Set
    End Property

    Public ReadOnly Property IsPlaying As Boolean Implements IAudioPlayer.IsPlaying
        Get
            SyncLock _audioLock
                Return _isPlaying
            End SyncLock
        End Get
    End Property

    Public ReadOnly Property OutputLatencyMs As Double Implements IAudioPlayer.OutputLatencyMs
        Get
            Return If(_backendType = NAudioBackend.Asio, 10.0, _wasapiBufferMs)
        End Get
    End Property

    Public Sub LoadStreaming(filePath As String) Implements IAudioPlayer.LoadStreaming
        ' Для мультитрека мы больше не стримим один файл. Просто очищаем старые кэши.
        SyncLock _audioLock
            _multitrackProvider?.ClearCache()
        End SyncLock
    End Sub

    Public Sub UnloadFile() Implements IAudioPlayer.UnloadFile
        StopPlayback()
        SyncLock _audioLock
            _multitrackProvider?.ClearCache()
        End SyncLock
    End Sub

    Public Sub Play(videoPosition As TimeSpan, audioOffset As TimeSpan) Implements IAudioPlayer.Play
        SyncLock _audioLock
            If _disposed OrElse _mainTrack Is Nothing Then Return
            Dim targetTime As TimeSpan = videoPosition - audioOffset
            If targetTime < TimeSpan.Zero Then targetTime = TimeSpan.Zero

            _mainTrack.SetPosition(targetTime)
            _mainTrack.Play()
            _isPlaying = True
        End SyncLock
    End Sub

    Public Sub Seek(videoPosition As TimeSpan, audioOffset As TimeSpan) Implements IAudioPlayer.Seek
        SyncLock _audioLock
            If _disposed OrElse _mainTrack Is Nothing Then Return
            Dim targetTime As TimeSpan = videoPosition - audioOffset
            If targetTime < TimeSpan.Zero Then targetTime = TimeSpan.Zero

            _mainTrack.SetPosition(targetTime)
        End SyncLock
    End Sub

    Public Sub Pause() Implements IAudioPlayer.Pause
        SyncLock _audioLock
            _mainTrack?.Pause()
            _isPlaying = False
        End SyncLock
    End Sub

    Public Sub ResumePlayback() Implements IAudioPlayer.ResumePlayback
        SyncLock _audioLock
            If _mainTrack IsNot Nothing Then
                _mainTrack.Play()
                _isPlaying = True
            End If
        End SyncLock
    End Sub

    Public Sub StopPlayback() Implements IAudioPlayer.StopPlayback
        SyncLock _audioLock
            If _mainTrack IsNot Nothing Then
                _mainTrack.Pause()
                _mainTrack.SetPosition(TimeSpan.Zero)
            End If
            _isPlaying = False
        End SyncLock
    End Sub

    Public Function GetCurrentPosition() As TimeSpan Implements IAudioPlayer.GetCurrentPosition
        SyncLock _audioLock
            Return If(_mainTrack?.CurrentTime, TimeSpan.Zero)
        End SyncLock
    End Function

    Public Sub SetSpeed(ratio As Single)
        SyncLock _audioLock
            If _mainTrack IsNot Nothing Then
                _mainTrack.PlaybackRate = Math.Max(0.5F, Math.Min(2.0F, ratio))
            End If
        End SyncLock
    End Sub

    Public Sub PlayScrubSample(pcmData As Byte(), offset As Integer, length As Integer, Optional sampleRate As Integer = 48000)
        SyncLock _audioLock
            If _disposed OrElse pcmData Is Nothing OrElse length = 0 Then Return
            If _scrubTrack IsNot Nothing Then _mixer.RemoveMixerInput(_scrubTrack)

            _scrubTrack = New FastScrubProvider(pcmData, offset, length, sampleRate, _volume)
            _mixer.AddMixerInput(_scrubTrack)
        End SyncLock
    End Sub

    Protected Overridable Sub Dispose(disposing As Boolean)
        If Not _disposed Then
            If disposing Then
                StopPlayback()
                SyncLock _audioLock
                    _multitrackProvider?.ClearCache()
                    Try
                        _waveOut?.Stop()
                        _waveOut?.Dispose()
                    Catch
                    End Try
                    _waveOut = Nothing
                End SyncLock
            End If
            _disposed = True
        End If
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        Dispose(True)
        GC.SuppressFinalize(Me)
    End Sub

    Public Function DisposeAsync() As ValueTask Implements IAsyncDisposable.DisposeAsync
        Dispose(True)
        GC.SuppressFinalize(Me)
        Return New ValueTask()
    End Function

    ' =========================================================================
    ' 1. ЯДРО NLE: Многодорожечный микшер клипов
    ' =========================================================================
    Private Class NleMultitrackProvider
        Implements ISampleProvider

        Public Property Model As ProjectModel
        Private ReadOnly _waveFormat As WaveFormat
        Private _currentTime As TimeSpan
        Private ReadOnly _lockObj As New Object()

        ' Кэш открытых ридеров для клипов
        Private ReadOnly _readers As New Dictionary(Of Guid, ClipAudioSource)()

        Public Sub New(sampleRate As Integer, channels As Integer)
            _waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels)
            _currentTime = TimeSpan.Zero
        End Sub

        Public ReadOnly Property WaveFormat As WaveFormat Implements ISampleProvider.WaveFormat
            Get
                Return _waveFormat
            End Get
        End Property

        Public ReadOnly Property CurrentTime As TimeSpan
            Get
                Return _currentTime
            End Get
        End Property

        Public Sub SetPosition(time As TimeSpan)
            SyncLock _lockObj
                _currentTime = time
            End SyncLock
        End Sub

        Public Sub ClearCache()
            SyncLock _lockObj
                For Each src In _readers.Values
                    src.Dispose()
                Next
                _readers.Clear()
            End SyncLock
        End Sub

        Private Function GetOrAddSource(clip As MediaClip) As ClipAudioSource
            Dim value As ClipAudioSource = Nothing
            If _readers.TryGetValue(clip.Id, value) Then Return value
            Dim src = New ClipAudioSource(clip.FilePath)
            _readers(clip.Id) = src
            Return src
        End Function

        Public Function Read(buffer() As Single, offset As Integer, count As Integer) As Integer Implements ISampleProvider.Read
            SyncLock _lockObj
                ' Сначала заполняем буфер тишиной
                Array.Clear(buffer, offset, count)
                If Model Is Nothing Then Return count

                Dim samplesToSeconds As Double = count / _waveFormat.SampleRate / _waveFormat.Channels
                Dim endTime As TimeSpan = _currentTime + TimeSpan.FromSeconds(samplesToSeconds)

                ' ИСПРАВЛЕНИЕ: Арендуем массив из пула вместо аллокации (Dim tempBuffer(count - 1) As Single)
                Dim tempBuffer = System.Buffers.ArrayPool(Of Single).Shared.Rent(count)

                Try
                    ' Обходим все активные дорожки
                    For Each track In Model.Tracks
                        If track.IsMuted Then Continue For
                        If track.Type <> TargetFormatType.Video AndAlso track.Type <> TargetFormatType.Audio Then Continue For

                        ' Ищем клипы, которые попадают в текущее временное окно (наш буфер)
                        For Each clip In track.Clips
                            If clip.TimelineStart < endTime AndAlso clip.TimelineEnd > _currentTime Then
                                Dim source = GetOrAddSource(clip)
                                If source IsNot Nothing AndAlso Not source.IsSilent Then

                                    Dim bufferOffsetSamples As Integer = 0
                                    Dim readStartTime As TimeSpan = _currentTime

                                    If _currentTime < clip.TimelineStart Then
                                        Dim blankSec = (clip.TimelineStart - _currentTime).TotalSeconds
                                        Dim framesOffset = CInt(Math.Floor(blankSec * _waveFormat.SampleRate))
                                        bufferOffsetSamples = framesOffset * _waveFormat.Channels
                                        readStartTime = clip.TimelineStart
                                    End If

                                    Dim clipLocalStart = readStartTime - clip.TimelineStart
                                    Dim expectedFileTime = clip.SourceIn + clipLocalStart

                                    If Math.Abs((source.CurrentTime - expectedFileTime).TotalSeconds) > 0.05 Then
                                        source.SetPosition(expectedFileTime)
                                    End If

                                    Dim availableTime = clip.TimelineEnd - readStartTime
                                    Dim maxReadSec = Math.Min(availableTime.TotalSeconds, samplesToSeconds - (bufferOffsetSamples / _waveFormat.SampleRate / _waveFormat.Channels))

                                    Dim framesToRead = CInt(Math.Ceiling(maxReadSec * _waveFormat.SampleRate))
                                    Dim samplesToRead = framesToRead * _waveFormat.Channels

                                    If samplesToRead + bufferOffsetSamples > count Then
                                        samplesToRead = count - bufferOffsetSamples
                                    End If

                                    samplesToRead = (samplesToRead \ _waveFormat.Channels) * _waveFormat.Channels

                                    If samplesToRead > 0 Then
                                        Array.Clear(tempBuffer, 0, samplesToRead)
                                        Dim samplesRead = source.Read(tempBuffer, 0, samplesToRead)

                                        If samplesRead > 0 Then
                                            ApplyClipVolumeAndFades(tempBuffer, samplesRead, clip, readStartTime)

                                            ' Смешиваем (суммируем) с основным выходным буфером
                                            For i As Integer = 0 To samplesRead - 1
                                                buffer(offset + bufferOffsetSamples + i) += tempBuffer(i)
                                            Next
                                        End If
                                    End If
                                End If
                            End If
                        Next
                    Next
                Finally
                    ' Обязательно возвращаем массив в пул, чтобы избежать утечек оперативной памяти!
                    System.Buffers.ArrayPool(Of Single).Shared.Return(tempBuffer)
                End Try

                _currentTime += TimeSpan.FromSeconds(samplesToSeconds)
                Return count
            End SyncLock
        End Function

        Private Sub ApplyClipVolumeAndFades(buffer() As Single, count As Integer, clip As MediaClip, currentTime As TimeSpan)
            Dim frames = count \ _waveFormat.Channels
            Dim clipStart = clip.TimelineStart
            Dim clipEnd = clip.TimelineEnd
            Dim fadeInDur = clip.FadeIn.TotalSeconds
            Dim fadeOutDur = clip.FadeOut.TotalSeconds
            Dim baseVol = clip.Volume

            For i As Integer = 0 To frames - 1
                Dim frameTime = currentTime + TimeSpan.FromSeconds(i / _waveFormat.SampleRate)
                Dim volMultiplier = baseVol

                If fadeInDur > 0 Then
                    Dim elapsedIn = (frameTime - clipStart).TotalSeconds
                    If elapsedIn < fadeInDur Then
                        volMultiplier *= CSng(Math.Max(0, Math.Min(1.0, elapsedIn / fadeInDur)))
                    End If
                End If

                If fadeOutDur > 0 Then
                    Dim remainingOut = (clipEnd - frameTime).TotalSeconds
                    If remainingOut < fadeOutDur Then
                        volMultiplier *= CSng(Math.Max(0, Math.Min(1.0, remainingOut / fadeOutDur)))
                    End If
                End If

                buffer(i * 2) *= volMultiplier
                buffer(i * 2 + 1) *= volMultiplier
            Next
        End Sub
    End Class

    ' =========================================================================
    ' 2. Источник конкретного клипа через MediaFoundation
    ' =========================================================================
    Private Class ClipAudioSource
        Implements IDisposable
        Private ReadOnly _reader As MediaFoundationReader
        Private ReadOnly _provider As ISampleProvider
        Public ReadOnly IsSilent As Boolean

        Public Sub New(filePath As String)
            Try
                ' Нативный декодер Windows: ест mp4, mkv, mp3, wav с минимальной задержкой
                _reader = New MediaFoundationReader(filePath)
                Dim floatProv As ISampleProvider = _reader.ToSampleProvider()

                If floatProv.WaveFormat.SampleRate <> 48000 Then
                    floatProv = New WdlResamplingSampleProvider(floatProv, 48000)
                End If

                If floatProv.WaveFormat.Channels = 1 Then
                    Dim multiplexer = New MultiplexingSampleProvider({floatProv}, 2)
                    multiplexer.ConnectInputToOutput(0, 0)
                    multiplexer.ConnectInputToOutput(0, 1)
                    floatProv = multiplexer
                End If

                _provider = floatProv
                IsSilent = False
            Catch ex As Exception
                Debug.WriteLine($"[ClipAudioSource] Ошибка загрузки {filePath}: {ex.Message}")
                IsSilent = True
            End Try
        End Sub

        Public ReadOnly Property CurrentTime As TimeSpan
            Get
                If IsSilent OrElse _reader Is Nothing Then Return TimeSpan.Zero
                Return _reader.CurrentTime
            End Get
        End Property

        Public Sub SetPosition(time As TimeSpan)
            If Not IsSilent AndAlso _reader IsNot Nothing Then
                _reader.CurrentTime = time
            End If
        End Sub

        Public Function Read(buffer() As Single, offset As Integer, count As Integer) As Integer
            If IsSilent OrElse _provider Is Nothing Then
                Array.Clear(buffer, offset, count)
                Return count
            End If
            Return _provider.Read(buffer, offset, count)
        End Function

        Public Sub Dispose() Implements IDisposable.Dispose
            _reader?.Dispose()
        End Sub
    End Class

    ' =========================================================================
    ' Обертки скраббинга и скорости
    ' =========================================================================
    Private Class DynamicSpeedSampleProvider
        Implements ISampleProvider

        Private Const MaxBufferSamples As Integer = 48000 * 2 * 2
        Private ReadOnly _tempBuffer(MaxBufferSamples - 1) As Single
        Private ReadOnly _leftoverBuffer(MaxBufferSamples - 1) As Single
        Private ReadOnly _source As NleMultitrackProvider
        Private ReadOnly _lockObj As New Object()
        Public Property Volume As Single = 1.0F
        Private _playbackRate As Single = 1.0F
        Private _isPlaying As Boolean = False
        Private _remainder As Single = 0.0F
        Private _leftoverSamples As Integer = 0

        Public Sub New(source As NleMultitrackProvider)
            _source = source
        End Sub

        Public Property PlaybackRate As Single
            Get
                Return _playbackRate
            End Get
            Set(value As Single)
                _playbackRate = Math.Max(0.5F, Math.Min(2.0F, value))
            End Set
        End Property

        Public ReadOnly Property WaveFormat As WaveFormat Implements ISampleProvider.WaveFormat
            Get
                Return _source?.WaveFormat
            End Get
        End Property

        Public ReadOnly Property CurrentTime As TimeSpan
            Get
                Return _source.CurrentTime
            End Get
        End Property

        Public Sub Play()
            _isPlaying = True
        End Sub

        Public Sub Pause()
            _isPlaying = False
        End Sub

        Public Sub SetPosition(time As TimeSpan)
            SyncLock _lockObj
                _source.SetPosition(time)
                _remainder = 0.0F
                _leftoverSamples = 0
            End SyncLock
        End Sub

        Public Function Read(buffer() As Single, offset As Integer, count As Integer) As Integer Implements ISampleProvider.Read
            SyncLock _lockObj
                If count = 0 OrElse _source Is Nothing Then Return 0
                If Not _isPlaying Then
                    Array.Clear(buffer, offset, count)
                    Return count
                End If

                If PlaybackRate = 1.0F AndAlso _leftoverSamples = 0 AndAlso Math.Abs(_remainder) < 0.0001F Then
                    Dim samplesReadFast = _source.Read(buffer, offset, count)
                    If samplesReadFast > 0 Then ApplyVolume(buffer, offset, samplesReadFast)
                    Return samplesReadFast
                End If

                Dim channels = WaveFormat.Channels
                Dim outFrameCount = count \ channels
                Dim requiredSrcFrames = CInt(Math.Ceiling(_remainder + (outFrameCount * PlaybackRate))) + 1
                Dim requiredSrcSamples = Math.Min(requiredSrcFrames * channels, MaxBufferSamples)
                Dim sourceOffset = 0

                If _leftoverSamples > 0 Then
                    Dim copyCount = Math.Min(_leftoverSamples, requiredSrcSamples)
                    Array.Copy(_leftoverBuffer, 0, _tempBuffer, 0, copyCount)
                    sourceOffset = copyCount
                End If

                Dim samplesToRead = requiredSrcSamples - sourceOffset
                Dim samplesRead = 0
                If samplesToRead > 0 Then samplesRead = _source.Read(_tempBuffer, sourceOffset, samplesToRead)

                Dim totalAvailableFrames = (sourceOffset + samplesRead) \ channels
                If totalAvailableFrames = 0 Then
                    Array.Clear(buffer, offset, count)
                    Return count
                End If

                Dim outFrame = 0
                While outFrame < outFrameCount
                    Dim srcFrameF = _remainder
                    Dim srcFrameI = CInt(Math.Floor(srcFrameF))
                    Dim srcFrameINext = srcFrameI + 1
                    Dim frac = srcFrameF - srcFrameI

                    If srcFrameINext >= totalAvailableFrames Then Exit While

                    For c As Integer = 0 To channels - 1
                        Dim val1 = _tempBuffer(srcFrameI * channels + c)
                        Dim val2 = _tempBuffer(srcFrameINext * channels + c)
                        buffer(offset + outFrame * channels + c) = val1 + (val2 - val1) * frac
                    Next

                    outFrame += 1
                    _remainder += PlaybackRate
                End While

                Dim consumedFrames = CInt(Math.Floor(_remainder))
                Dim remainingSamples = (sourceOffset + samplesRead) - (consumedFrames * channels)

                If remainingSamples > 0 AndAlso outFrame > 0 Then
                    Array.Copy(_tempBuffer, consumedFrames * channels, _leftoverBuffer, 0, remainingSamples)
                    _leftoverSamples = remainingSamples
                    _remainder -= consumedFrames
                Else
                    _leftoverSamples = 0
                    _remainder = If(outFrame = 0, 0, _remainder - consumedFrames)
                End If

                ApplyVolume(buffer, offset, outFrame * channels)
                If outFrame * channels < count Then Array.Clear(buffer, offset + outFrame * channels, count - outFrame * channels)

                Return count
            End SyncLock
        End Function

        Private Sub ApplyVolume(buffer() As Single, offset As Integer, count As Integer)
            If Volume <> 1.0F Then
                For i As Integer = 0 To count - 1
                    buffer(offset + i) *= Volume
                Next
            End If
        End Sub
    End Class

    Private Class FastScrubProvider
        Implements ISampleProvider
        Private ReadOnly _pcmData As Byte()
        Private ReadOnly _offset As Integer
        Private ReadOnly _length As Integer
        Private ReadOnly _waveFormat As WaveFormat
        Private _position As Integer
        Public Property Volume As Single

        Public Sub New(pcmData As Byte(), offset As Integer, length As Integer, sampleRate As Integer, volume As Single)
            _pcmData = pcmData : _offset = offset : _length = length : Me.Volume = volume
            _waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2)
        End Sub

        Public ReadOnly Property WaveFormat As WaveFormat Implements ISampleProvider.WaveFormat
            Get
                Return _waveFormat
            End Get
        End Property

        Public Function Read(buffer As Single(), offset As Integer, count As Integer) As Integer Implements ISampleProvider.Read
            Dim samplesToRead As Integer = Math.Min(count, (_length - _position) \ 2)
            If samplesToRead <= 0 Then Return 0
            For i As Integer = 0 To samplesToRead - 1
                Dim sampleFloat As Single = (BitConverter.ToInt16(_pcmData, _offset + _position + (i * 2)) / 32768.0F) * Volume
                buffer(offset + i * 2) = sampleFloat
                buffer(offset + i * 2 + 1) = sampleFloat
            Next
            _position += samplesToRead * 2
            Return samplesToRead * 2
        End Function
    End Class
End Class