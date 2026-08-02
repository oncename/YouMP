Option Strict On
Option Explicit On

Imports System
Imports System.Diagnostics
Imports System.Threading
Imports System.Threading.Tasks

Namespace yoump

    ' ОРКЕСТРАТОР ВОСПРОИЗВЕДЕНИЯ (Мастер-часы NLE)
    Public Class PlaybackController
        Implements IServices.IPlaybackController, IDisposable

        Public Event TimeChanged As EventHandler(Of TimeSpan) Implements IServices.IPlaybackController.TimeChanged
        Public Event PlaybackStopped As EventHandler Implements IServices.IPlaybackController.PlaybackStopped
        Public Event PlaybackError As EventHandler(Of String) Implements IServices.IPlaybackController.PlaybackError
        Public Event MarkerReached As EventHandler Implements IServices.IPlaybackController.MarkerReached

        Private ReadOnly _vlc As IServices.IMediaPlayerManager
        Private ReadOnly _audio As IServices.IAudioPlayer
        Private ReadOnly _model As ProjectModel

        Private _audioOffset As TimeSpan = TimeSpan.Zero
        Private _lastVolumePercent As Integer = 100
        Private _disposedValue As Boolean = False

        ' Главные часы идут строго по виртуальному времени (таймлайну)
        Private _lastValidVirtTime As TimeSpan = TimeSpan.Zero
        Private _dawVirtualStartTime As TimeSpan = TimeSpan.Zero
        Private ReadOnly _dawClock As New Stopwatch()

        Private _state As IServices.PlaybackState = IServices.PlaybackState.Stopped
        Private ReadOnly _volumeSmoother As TimeIndependentVolumeSmoother
        Private _audioFailed As Boolean = False

        Public Sub New(vlc As IServices.IMediaPlayerManager, audio As IServices.IAudioPlayer, model As ProjectModel)
            ArgumentNullException.ThrowIfNull(vlc)
            ArgumentNullException.ThrowIfNull(audio)
            ArgumentNullException.ThrowIfNull(model)

            _vlc = vlc
            _audio = audio
            _model = model

            _volumeSmoother = New TimeIndependentVolumeSmoother(_lastVolumePercent / 100.0, 30.0)

            AddHandler _vlc.PlaybackStopped, AddressOf OnVlcStopped
            AddHandler _vlc.PlaybackError, AddressOf OnVlcError

            ' Инъекция модели в аудио-микшер
            Dim syncPlayer = TryCast(_audio, NAudioSyncPlayer)
            If syncPlayer IsNot Nothing Then
                AddHandler syncPlayer.AudioError, AddressOf OnAudioError
                syncPlayer.ProjectModel = _model
            End If

            ' Инъекция модели в видео-микшер
            Dim videoPlayer = TryCast(_vlc, Direct3D11VideoPlayer)
            If videoPlayer IsNot Nothing Then
                videoPlayer.ProjectModel = _model
            End If

            _vlc.ExternalClock = AddressOf GetMasterVirtualTime
        End Sub

        Private Sub OnAudioError(sender As Object, message As String)
            If _disposedValue Then Return
            _audioFailed = True
            Debug.WriteLine($"[PlaybackController] Переход в Fallback-режим: {message}")
            RaiseEvent PlaybackError(Me, $"Аудио переведено в безопасный режим: {message}")
        End Sub

        ' Получение точного времени таймлайна с компенсацией аппаратных задержек
        Private Function GetMasterVirtualTime() As TimeSpan
            If _state <> IServices.PlaybackState.Playing Then Return _lastValidVirtTime
            Dim idealTime As TimeSpan = _dawVirtualStartTime + _dawClock.Elapsed

            ' Жесткая синхронизация по аппаратному буферу звуковой карты (чтобы видео не убегало от звука)
            If Not _audioFailed AndAlso _audio IsNot Nothing AndAlso _audio.IsPlaying Then
                Dim latencyMs As Double = _audio.OutputLatencyMs
                Dim rawAudioTimeMs As Double = _audio.GetCurrentPosition().TotalMilliseconds

                Dim compensatedAudioMs As Double = Math.Max(0.0, rawAudioTimeMs - latencyMs)
                Dim hwVirtTime As TimeSpan = TimeSpan.FromMilliseconds(compensatedAudioMs) + _audioOffset

                ' Мягкая подгонка часов DAW под аппаратный звук
                If Math.Abs((idealTime - hwVirtTime).TotalMilliseconds) > 50 Then
                    _dawVirtualStartTime = hwVirtTime - _dawClock.Elapsed
                    Return hwVirtTime
                End If
            End If

            Return idealTime
        End Function

        Public ReadOnly Property State As IServices.PlaybackState Implements IServices.IPlaybackController.State
            Get
                Return _state
            End Get
        End Property

        Public ReadOnly Property IsPlaying As Boolean Implements IServices.IPlaybackController.IsPlaying
            Get
                Return _state = IServices.PlaybackState.Playing
            End Get
        End Property

        Public ReadOnly Property CurrentVirtualTime As TimeSpan Implements IServices.IPlaybackController.CurrentVirtualTime
            Get
                If IsPlaying Then
                    _lastValidVirtTime = GetMasterVirtualTime()
                End If
                Return _lastValidVirtTime
            End Get
        End Property

        ' Legacy для интерфейса (возвращает локальное время, хотя контроллер мыслит глобальным)
        Public ReadOnly Property CurrentPhysicalTime As TimeSpan Implements IServices.IPlaybackController.CurrentPhysicalTime
            Get
                Return CurrentVirtualTime
            End Get
        End Property

        Public Async Function PlayAsync(filePath As String, startVirtualTime As TimeSpan, Optional externalAudio As String = "", Optional cancellationToken As CancellationToken = Nothing) As Task Implements IServices.IPlaybackController.PlayAsync
            _state = IServices.PlaybackState.Playing
            _dawVirtualStartTime = startVirtualTime
            _dawClock.Restart()
            _audioFailed = False

            ' Говорим видео-плееру начать работу (он сам подтянет нужные файлы из ProjectModel)
            Dim success As Boolean = Await _vlc.PlayAsync("", startVirtualTime, "", "")

            If success AndAlso Not cancellationToken.IsCancellationRequested Then
                _volumeSmoother.Reset(_lastVolumePercent / 100.0)
                _audio.Volume = _lastVolumePercent

                _audio.Play(startVirtualTime, _audioOffset)
                _lastValidVirtTime = startVirtualTime
            Else
                If success Then _vlc.StopPlayback()
                _state = IServices.PlaybackState.Stopped
                _dawClock.Stop()
            End If
        End Function

        Public Sub Pause() Implements IServices.IPlaybackController.Pause
            If _state = IServices.PlaybackState.Playing Then
                _dawClock.Stop()
                _vlc.Pause()
                _audio.Pause()
                _state = IServices.PlaybackState.Paused
                _lastValidVirtTime = GetMasterVirtualTime()
            End If
        End Sub

        Public Sub ResumePlayback() Implements IServices.IPlaybackController.ResumePlayback
            If _state = IServices.PlaybackState.Paused Then
                _dawVirtualStartTime = _lastValidVirtTime
                _dawClock.Restart()

                _vlc.ResumePlayback()
                _state = IServices.PlaybackState.Playing

                If Not _audioFailed Then
                    Try
                        _audio.Seek(_lastValidVirtTime, _audioOffset)
                        _audio.ResumePlayback()
                    Catch ex As Exception
                        Debug.WriteLine($"Ошибка ResumePlayback: {ex.Message}")
                    End Try
                End If
            End If
        End Sub

        Public Sub StopPlayback() Implements IServices.IPlaybackController.StopPlayback
            If _disposedValue Then Return
            _dawClock.Stop()
            _vlc.StopPlayback()
            _audio.StopPlayback()
            _state = IServices.PlaybackState.Stopped
        End Sub

        Public Sub Seek(virtualTime As TimeSpan) Implements IServices.IPlaybackController.Seek
            If _disposedValue Then Return

            _lastValidVirtTime = virtualTime
            _dawVirtualStartTime = virtualTime
            If IsPlaying Then _dawClock.Restart()

            If _state = IServices.PlaybackState.Playing OrElse _state = IServices.PlaybackState.Paused Then
                _vlc.Seek(virtualTime)
            Else
                _vlc.StopPlayback()
            End If

            If Not _audioFailed Then
                If _state = IServices.PlaybackState.Playing Then
                    _audio.Play(virtualTime, _audioOffset)
                Else
                    _audio.Seek(virtualTime, _audioOffset)
                End If
            End If

            RaiseEvent TimeChanged(Me, virtualTime)
        End Sub

        Public Sub SetVolume(percent As Integer) Implements IServices.IPlaybackController.SetVolume
            _lastVolumePercent = percent
            If Not IsPlaying Then
                _volumeSmoother.Reset(percent / 100.0)
                _audio.Volume = percent
            End If
        End Sub

        Public Sub SetRate(rate As Single) Implements IServices.IPlaybackController.SetRate
            _vlc.Rate = rate
        End Sub

        Public Sub SetAudioOffset(offset As TimeSpan) Implements IServices.IPlaybackController.SetAudioOffset
            _audioOffset = offset
            If IsPlaying AndAlso Not _audioFailed Then
                _audio.Seek(CurrentVirtualTime, _audioOffset)
                If Not _audio.IsPlaying Then _audio.ResumePlayback()
            End If
        End Sub

        Public Sub SetAudioDelay(delayUs As Long) Implements IServices.IPlaybackController.SetAudioDelay
        End Sub

        Public Sub ProcessTick() Implements IServices.IPlaybackController.ProcessTick
            If _state <> IServices.PlaybackState.Playing Then Return

            Dim targetVolDouble As Double = _lastVolumePercent / 100.0
            Dim smoothedVolDouble As Double = _volumeSmoother.Update(targetVolDouble)
            _audio.Volume = CInt(Math.Round(smoothedVolDouble * 100.0))

            Dim virtTime As TimeSpan = GetMasterVirtualTime()
            _lastValidVirtTime = virtTime

            Dim stopVirtTime As TimeSpan = _model.PhysicalToVirtualTime(If(_model.IsZoomed, _model.ViewEnd, _model.MarkerEnd))

            If virtTime >= stopVirtTime Then
                StopPlayback()
                RaiseEvent TimeChanged(Me, stopVirtTime)
                RaiseEvent MarkerReached(Me, EventArgs.Empty)
                Return
            End If

            RaiseEvent TimeChanged(Me, virtTime)
        End Sub

        Private Sub OnVlcStopped(sender As Object, e As EventArgs)
            If _disposedValue Then Return

            _audio.StopPlayback()
            _dawClock.Stop()
            _state = IServices.PlaybackState.Stopped

            Dim stopVirtTime As TimeSpan = _model.PhysicalToVirtualTime(If(_model.IsZoomed, _model.ViewEnd, _model.MarkerEnd))
            If Math.Abs((_lastValidVirtTime - stopVirtTime).TotalMilliseconds) < 600 Then
                _lastValidVirtTime = stopVirtTime
            End If

            RaiseEvent TimeChanged(Me, _lastValidVirtTime)
            RaiseEvent PlaybackStopped(Me, EventArgs.Empty)
        End Sub

        Private Sub OnVlcError(message As String)
            If _disposedValue Then Return
            _dawClock.Stop()
            _state = IServices.PlaybackState.Error
            RaiseEvent PlaybackError(Me, message)
        End Sub

        Protected Overridable Sub Dispose(disposing As Boolean)
            If Not _disposedValue Then
                If disposing Then
                    _dawClock.Stop()
                    RemoveHandler _vlc.PlaybackStopped, AddressOf OnVlcStopped
                    RemoveHandler _vlc.PlaybackError, AddressOf OnVlcError

                    Dim syncPlayer = TryCast(_audio, NAudioSyncPlayer)
                    If syncPlayer IsNot Nothing Then
                        RemoveHandler syncPlayer.AudioError, AddressOf OnAudioError
                    End If
                End If
                _disposedValue = True
            End If
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            Dispose(disposing:=True)
            GC.SuppressFinalize(Me)
        End Sub
    End Class
End Namespace