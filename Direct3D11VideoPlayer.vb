' Path: Direct3D11VideoPlayer.vb
Option Strict On
Option Explicit On

Imports System.Collections.Concurrent
Imports System.Collections.Generic
Imports System.Diagnostics
Imports System.Drawing
Imports System.Linq
Imports System.Runtime.InteropServices
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Windows.Forms
Imports SharpDX
Imports SharpDX.Direct3D
Imports SharpDX.Direct3D11
Imports SharpDX.DXGI
Imports SharpDX.Mathematics.Interop
Imports yoump.IServices
Imports yoump.Native
Imports D2D1 = SharpDX.Direct2D1

Public NotInheritable Class Direct3D11VideoPlayer
    Implements IMediaPlayerManager
    Implements IAsyncDisposable

    <DllImport("winmm.dll", EntryPoint:="timeBeginPeriod")>
    Private Shared Function TimeBeginPeriod(uPeriod As UInteger) As UInteger
    End Function

    <DllImport("winmm.dll", EntryPoint:="timeEndPeriod")>
    Private Shared Function TimeEndPeriod(uPeriod As UInteger) As UInteger
    End Function

    Private Const TIMERR_NOERROR As UInteger = 0

    Public Event PlaybackError(message As String) Implements IMediaPlayerManager.PlaybackError
    Public Event PlaybackStopped As EventHandler Implements IMediaPlayerManager.PlaybackStopped
    Public Event LengthChanged(length As TimeSpan) Implements IMediaPlayerManager.LengthChanged
    Public Event LogMessage(message As String) Implements IMediaPlayerManager.LogMessage

    Public Property ProjectModel As ProjectModel

    Private Class VideoFrame
        Implements IDisposable
        Public PtsMs As Double
        Public Buffer As GpuVideoFrame
        Public Sub Dispose() Implements IDisposable.Dispose
            If Buffer IsNot Nothing Then
                Buffer.Dispose()
                Buffer = Nothing
            End If
        End Sub
    End Class

    Private Structure DrawCommand
        Public Frame As VideoFrame
        Public Opacity As Single
        Public Scale As Single
        Public PosX As Single
        Public PosY As Single
        Public Rotation As Single
    End Structure

    Private _basePath As String
    Private _uiControl As Control
    Private _disposed As Boolean = False

    Private _d3dDevice As SharpDX.Direct3D11.Device
    Private _swapChain As SwapChain
    Private _d2dFactory As D2D1.Factory
    Private _d2dRenderTarget As D2D1.RenderTarget

    Private ReadOnly _renderLock As New Object()
    Private _d2dErrorCount As Integer = 0
    Private Const MAX_D2D_ERRORS As Integer = 3

    Private ReadOnly _timeLock As New Object()

    Private _state As IServices.PlaybackState = IServices.PlaybackState.Stopped
    Private ReadOnly _stopwatch As New Stopwatch()
    Private _startOffsetMs As Double = 0.0
    Private _opacity As Single = 1.0F
    Private _volume As Integer = 100
    Private _rate As Single = 1.0F

    Private _forceRedraw As Boolean = False

    Private _renderCts As CancellationTokenSource
    Private _renderTask As Task
    Private _orchestratorTask As Task

    Private _isScrubbing As Boolean = False
    Private _currentHwnd As IntPtr = IntPtr.Zero

    Public Property ExternalClock As Func(Of TimeSpan) Implements IMediaPlayerManager.ExternalClock
    Private _lastAudioHwTimeMs As Double = -1.0
    Private ReadOnly _audioHwStopwatch As New Stopwatch()
    Private ReadOnly _d2dBitmapCache As New Dictionary(Of IntPtr, D2D1.Bitmap)()

    Private ReadOnly _streamers As New Dictionary(Of Guid, VideoStreamer)()
    Private ReadOnly _streamersLock As New Object()

    Public Function GetDevice() As SharpDX.Direct3D11.Device
        SyncLock _renderLock
            EnsureD3D11Resources()
            Return _d3dDevice
        End SyncLock
    End Function

    Public ReadOnly Property IsPlaying As Boolean Implements IMediaPlayerManager.IsPlaying
        Get
            SyncLock _timeLock
                Return _state = IServices.PlaybackState.Playing
            End SyncLock
        End Get
    End Property

    Public ReadOnly Property CurrentTimeMs As Long Implements IMediaPlayerManager.CurrentTimeMs
        Get
            SyncLock _timeLock
                If _state = IServices.PlaybackState.Stopped Then Return 0
                Return CLng(_startOffsetMs + (_stopwatch.ElapsedMilliseconds * _rate))
            End SyncLock
        End Get
    End Property

    Public ReadOnly Property State As IServices.PlaybackState Implements IMediaPlayerManager.State
        Get
            SyncLock _timeLock
                Return _state
            End SyncLock
        End Get
    End Property

    Public Property Volume As Integer Implements IMediaPlayerManager.Volume
        Get
            Return _volume
        End Get
        Set(value As Integer)
            _volume = Math.Max(0, Math.Min(100, value))
        End Set
    End Property

    Public Property Rate As Single Implements IMediaPlayerManager.Rate
        Get
            SyncLock _timeLock
                Return _rate
            End SyncLock
        End Get
        Set(value As Single)
            SyncLock _timeLock
                If _state = IServices.PlaybackState.Playing OrElse _state = IServices.PlaybackState.Paused Then
                    _startOffsetMs += (_stopwatch.ElapsedMilliseconds * _rate)
                    _stopwatch.Restart()
                    If _state = IServices.PlaybackState.Paused Then _stopwatch.Stop()
                End If
                _rate = value
            End SyncLock
        End Set
    End Property

    Public Sub SetVolume(percent As Integer) Implements IMediaPlayerManager.SetVolume
        Volume = percent
    End Sub

    Public Sub SetAudioDelay(delayUs As Long) Implements IMediaPlayerManager.SetAudioDelay
    End Sub

    Public Sub SetVideoOpacity(opacity As Single) Implements IMediaPlayerManager.SetVideoOpacity
        _opacity = Math.Max(0.0F, Math.Min(1.0F, opacity))
    End Sub

    Public Sub RefreshComposition()
        _forceRedraw = True
    End Sub

    Public Sub Initialize(basePath As String, videoView As Control) Implements IMediaPlayerManager.Initialize
        _basePath = basePath
        _uiControl = videoView

        SyncLock _renderLock
            EnsureD3D11Resources()
        End SyncLock

        _renderCts = New CancellationTokenSource()
        _orchestratorTask = Task.Factory.StartNew(AddressOf OrchestratorLoop, _renderCts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default)
        _renderTask = Task.Factory.StartNew(AddressOf RenderLoop, _renderCts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default)
    End Sub

    Private Sub EnsureD3D11Resources()
        If _uiControl Is Nothing OrElse _uiControl.IsDisposed OrElse Not _uiControl.IsHandleCreated Then Return
        Dim currentHandle As IntPtr
        Try
            currentHandle = _uiControl.Handle
        Catch
            Return
        End Try

        If currentHandle = IntPtr.Zero Then Return
        If currentHandle <> _currentHwnd Then _d2dErrorCount = 0
        If _d2dErrorCount >= MAX_D2D_ERRORS Then Return

        If _swapChain Is Nothing OrElse currentHandle <> _currentHwnd Then
            Try
                Dim clientSize As Size
                Try
                    clientSize = _uiControl.ClientSize
                Catch
                    clientSize = New Size(1, 1)
                End Try

                If clientSize.Width <= 0 OrElse clientSize.Height <= 0 Then Return

                For Each bmp In _d2dBitmapCache.Values
                    Try
                        If bmp IsNot Nothing AndAlso Not bmp.IsDisposed Then bmp.Dispose()
                    Catch
                    End Try
                Next
                _d2dBitmapCache.Clear()

                If _d2dRenderTarget IsNot Nothing Then
                    _d2dRenderTarget.Dispose()
                    _d2dRenderTarget = Nothing
                End If

                If _swapChain IsNot Nothing Then
                    Try
                        _swapChain.Dispose()
                    Catch
                    End Try
                    _swapChain = Nothing
                End If

                If _d2dFactory IsNot Nothing Then
                    _d2dFactory.Dispose()
                    _d2dFactory = Nothing
                End If

                Dim desc As New SwapChainDescription() With {
                    .BufferCount = 2,
                    .ModeDescription = New ModeDescription(clientSize.Width, clientSize.Height, New Rational(60, 1), SharpDX.DXGI.Format.B8G8R8A8_UNorm),
                    .IsWindowed = True,
                    .OutputHandle = currentHandle,
                    .SampleDescription = New SampleDescription(1, 0),
                    .SwapEffect = SwapEffect.Discard,
                    .Usage = Usage.RenderTargetOutput
                }

                If _d3dDevice Is Nothing Then
                    SharpDX.Direct3D11.Device.CreateWithSwapChain(DriverType.Hardware, DeviceCreationFlags.BgraSupport, desc, _d3dDevice, _swapChain)
                    Try
                        Using multithread = _d3dDevice.QueryInterface(Of SharpDX.Direct3D11.Multithread)()
                            multithread?.SetMultithreadProtected(True)
                        End Using
                    Catch ex As Exception
                        RaiseEvent LogMessage($"Предупреждение D3D11 Multithread: {ex.Message}")
                    End Try
                Else
                    Using dxgiDevice As DXGI.Device = _d3dDevice.QueryInterface(Of DXGI.Device)()
                        Using adapter As DXGI.Adapter = dxgiDevice.Adapter
                            Using factory As DXGI.Factory = adapter.GetParent(Of DXGI.Factory)()
                                _swapChain = New SwapChain(factory, _d3dDevice, desc)
                            End Using
                        End Using
                    End Using
                End If

                _currentHwnd = currentHandle
                _d2dFactory = New D2D1.Factory(D2D1.FactoryType.MultiThreaded)

                Using backBuffer As Texture2D = _swapChain.GetBackBuffer(Of Texture2D)(0)
                    Using dxgiSurface As DXGI.Surface = backBuffer.QueryInterface(Of DXGI.Surface)()
                        Dim rtp As New D2D1.RenderTargetProperties(New D2D1.PixelFormat(SharpDX.DXGI.Format.B8G8R8A8_UNorm, D2D1.AlphaMode.Premultiplied))
                        _d2dRenderTarget = New D2D1.RenderTarget(_d2dFactory, dxgiSurface, rtp) With {.AntialiasMode = D2D1.AntialiasMode.PerPrimitive}
                    End Using
                End Using
                _d2dErrorCount = 0
            Catch ex As SharpDXException
                _d2dErrorCount += 1
                RaiseEvent LogMessage($"Ошибка DXGI: {ex.Message}")
            Catch ex As Exception
                _d2dErrorCount += 1
                RaiseEvent LogMessage($"Ошибка D3D11: {ex.Message}")
            End Try
        End If
    End Sub

    Private Function GetActiveVideoClips(virtTime As TimeSpan) As List(Of MediaClip)
        Dim activeClips As New List(Of MediaClip)()
        If ProjectModel Is Nothing Then Return activeClips

        For i As Integer = ProjectModel.Tracks.Count - 1 To 0 Step -1
            Dim track = ProjectModel.Tracks(i)
            If track.Type = TargetFormatType.Video AndAlso Not track.IsMuted Then
                Dim clip = track.GetClipAtTime(virtTime)
                If clip IsNot Nothing Then
                    activeClips.Add(clip)
                End If
            End If
        Next
        Return activeClips
    End Function

    Private Sub OrchestratorLoop()
        While Not _disposed AndAlso Not _renderCts.IsCancellationRequested
            If _state = IServices.PlaybackState.Stopped OrElse ProjectModel Is Nothing OrElse _d3dDevice Is Nothing Then
                Thread.Sleep(50)
                Continue While
            End If

            Dim currentVirtSec As Double
            SyncLock _timeLock
                Dim videoSwTime As Double = _stopwatch.ElapsedMilliseconds * _rate
                currentVirtSec = (_startOffsetMs + videoSwTime) / 1000.0
            End SyncLock

            Dim lookaheadSec = currentVirtSec + 2.5

            SyncLock _streamersLock
                Dim neededClips As New HashSet(Of Guid)()
                For Each track In ProjectModel.Tracks
                    If track.Type = TargetFormatType.Video AndAlso Not track.IsMuted Then
                        For Each clip In track.Clips
                            If clip.TimelineStart.TotalSeconds < lookaheadSec AndAlso clip.TimelineEnd.TotalSeconds > currentVirtSec - 0.5 Then
                                neededClips.Add(clip.Id)
                                If Not _streamers.ContainsKey(clip.Id) Then
                                    Dim streamer = New VideoStreamer(clip)
                                    Dim startLocalTime = clip.SourceIn
                                    If currentVirtSec > clip.TimelineStart.TotalSeconds Then
                                        startLocalTime += TimeSpan.FromSeconds(currentVirtSec - clip.TimelineStart.TotalSeconds)
                                    End If
                                    streamer.Start(_d3dDevice, _d3dDevice.ImmediateContext, startLocalTime)
                                    _streamers.Add(clip.Id, streamer)
                                End If
                            End If
                        Next
                    End If
                Next

                Dim toRemove = _streamers.Keys.Where(Function(k) Not neededClips.Contains(k)).ToList()
                For Each k In toRemove
                    _streamers(k).Dispose()
                    _streamers.Remove(k)
                Next
            End SyncLock

            Thread.Sleep(50)
        End While
    End Sub

    Public Function PlayAsync(filePath As String, startTime As TimeSpan, Optional externalAudioPath As String = "", Optional videoFilter As String = "") As Task(Of Boolean) Implements IMediaPlayerManager.PlayAsync
        If _disposed Then Return Task.FromResult(False)
        _isScrubbing = False

        ' === ИСПРАВЛЕНИЕ: Принудительно сбрасываем позицию декодеров. ===
        ' Иначе при повторном старте они так и останутся висеть в состоянии конца файла.
        Seek(startTime)

        SyncLock _timeLock
            _startOffsetMs = startTime.TotalMilliseconds
            _lastAudioHwTimeMs = -1.0
            _audioHwStopwatch.Restart()
            _state = IServices.PlaybackState.Playing
            _stopwatch.Restart()
        End SyncLock

        Return Task.FromResult(True)
    End Function

    Public Sub Play(filePath As String, Optional externalAudioPath As String = "") Implements IMediaPlayerManager.Play
        Dim discardTask As Task = PlayAsync(filePath, TimeSpan.Zero, externalAudioPath).ContinueWith(Sub(t)
                                                                                                         If t.IsFaulted Then
                                                                                                             RaiseEvent PlaybackError(t.Exception.GetBaseException().Message)
                                                                                                         End If
                                                                                                     End Sub)
    End Sub

    Public Sub Pause() Implements IMediaPlayerManager.Pause
        SyncLock _timeLock
            If _state = IServices.PlaybackState.Playing Then
                _stopwatch.Stop()
                _state = IServices.PlaybackState.Paused
            End If
        End SyncLock
    End Sub

    Public Sub ResumePlayback() Implements IMediaPlayerManager.ResumePlayback
        SyncLock _timeLock
            If _state = IServices.PlaybackState.Paused Then
                _lastAudioHwTimeMs = -1.0
                _audioHwStopwatch.Restart()
                _stopwatch.Start()
                _state = IServices.PlaybackState.Playing
            End If
        End SyncLock
    End Sub

    Public Sub Seek(targetTime As TimeSpan) Implements IMediaPlayerManager.Seek
        If _disposed Then Return

        SyncLock _timeLock
            _startOffsetMs = targetTime.TotalMilliseconds
            _lastAudioHwTimeMs = -1.0
            _audioHwStopwatch.Restart()
            If _state = IServices.PlaybackState.Playing Then
                _stopwatch.Restart()
            Else
                _stopwatch.Reset()
            End If
        End SyncLock

        SyncLock _streamersLock
            For Each kvp In _streamers
                Dim clip = kvp.Value.Clip
                If targetTime >= clip.TimelineStart AndAlso targetTime <= clip.TimelineEnd Then
                    Dim localTime = clip.SourceIn + (targetTime - clip.TimelineStart)
                    kvp.Value.RequestSeek(localTime)
                End If
            Next
        End SyncLock
    End Sub

    Public Sub StopPlayback() Implements IMediaPlayerManager.StopPlayback
        SyncLock _timeLock
            If _state = IServices.PlaybackState.Stopped Then Return
            _state = IServices.PlaybackState.Stopped
            _stopwatch.Stop()
            _startOffsetMs = 0.0
            _lastAudioHwTimeMs = -1.0
        End SyncLock

        SyncLock _renderLock
            If _d2dRenderTarget IsNot Nothing AndAlso Not _disposed Then
                Try
                    _d2dRenderTarget.BeginDraw()
                    _d2dRenderTarget.Clear(New RawColor4(0, 0, 0, 1.0F))
                    _d2dRenderTarget.EndDraw()
                    _swapChain?.Present(0, PresentFlags.None)
                Catch ex As Exception
                End Try
            End If
        End SyncLock

        RaiseEvent PlaybackStopped(Me, EventArgs.Empty)
    End Sub

    Private Sub RenderLoop()
        Dim beginPeriodResult As UInteger = TimeBeginPeriod(1)
        Dim isHighResTimerActive As Boolean = (beginPeriodResult = TIMERR_NOERROR)

        Try
            Dim lastDrawnPts As Double = -1.0

            While Not _disposed AndAlso Not _renderCts.IsCancellationRequested
                Dim currentState As IServices.PlaybackState
                Dim currentVideoTimeMs As Double = 0.0
                Dim masterTimeMs As Double = 0.0

                SyncLock _timeLock
                    currentState = _state
                    If currentState = IServices.PlaybackState.Playing OrElse currentState = IServices.PlaybackState.Paused Then
                        Dim videoSwTime As Double = _stopwatch.ElapsedMilliseconds * _rate
                        currentVideoTimeMs = _startOffsetMs + videoSwTime

                        If currentState = IServices.PlaybackState.Playing AndAlso ExternalClock IsNot Nothing Then
                            Dim extTime = ExternalClock.Invoke()
                            If extTime <> TimeSpan.MinValue Then
                                Dim rawAudioMs As Double = extTime.TotalMilliseconds
                                If rawAudioMs <> _lastAudioHwTimeMs Then
                                    _lastAudioHwTimeMs = rawAudioMs
                                    _audioHwStopwatch.Restart()
                                End If
                                Dim interpolatedAudioMs As Double = _lastAudioHwTimeMs + Math.Min(_audioHwStopwatch.ElapsedMilliseconds * _rate, 50.0)
                                masterTimeMs = interpolatedAudioMs

                                Dim drift As Double = masterTimeMs - currentVideoTimeMs
                                If Math.Abs(drift) > 100.0 Then
                                    _startOffsetMs = masterTimeMs - videoSwTime
                                    currentVideoTimeMs = masterTimeMs
                                Else
                                    _startOffsetMs += drift * 0.02
                                    currentVideoTimeMs += drift * 0.02
                                End If
                            Else
                                masterTimeMs = currentVideoTimeMs
                            End If
                        Else
                            masterTimeMs = currentVideoTimeMs
                        End If
                    End If
                End SyncLock

                If _isScrubbing OrElse currentState = IServices.PlaybackState.Stopped Then
                    Thread.Sleep(10)
                    Continue While
                End If

                If currentState = IServices.PlaybackState.Playing OrElse currentState = IServices.PlaybackState.Paused Then

                    Dim activeClips = GetActiveVideoClips(TimeSpan.FromMilliseconds(currentVideoTimeMs))
                    Dim framesToDraw As New List(Of DrawCommand)()
                    Dim currentPtsSum As Double = 0

                    For Each activeClip In activeClips
                        Dim localTimeMs = activeClip.SourceIn.TotalMilliseconds + (currentVideoTimeMs - activeClip.TimelineStart.TotalMilliseconds)
                        Dim streamer As VideoStreamer = Nothing

                        SyncLock _streamersLock
                            Dim value As VideoStreamer = Nothing
                            If _streamers.TryGetValue(activeClip.Id, value) Then streamer = value
                        End SyncLock

                        If streamer IsNot Nothing Then
                            Dim frameToDraw As VideoFrame = Nothing
                            Dim peekFrame As VideoFrame = Nothing

                            While streamer.Queue.TryPeek(peekFrame)
                                If peekFrame.PtsMs < localTimeMs - 50.0 AndAlso streamer.Queue.Count > 1 Then
                                    Dim drop As VideoFrame = Nothing
                                    If streamer.Queue.TryDequeue(drop) Then drop.Dispose()
                                    Continue While
                                End If
                                If peekFrame.PtsMs <= localTimeMs + 15.0 Then
                                    Dim deq As VideoFrame = Nothing
                                    If streamer.Queue.TryDequeue(deq) Then
                                        If frameToDraw IsNot Nothing Then frameToDraw.Dispose()
                                        frameToDraw = deq
                                    End If
                                Else
                                    Exit While
                                End If
                            End While

                            If frameToDraw IsNot Nothing Then
                                Dim clipOpacity As Single = 1.0F
                                Dim virtTime = TimeSpan.FromMilliseconds(currentVideoTimeMs)

                                If activeClip.FadeIn > TimeSpan.Zero AndAlso virtTime < activeClip.TimelineStart + activeClip.FadeIn Then
                                    clipOpacity = CSng((virtTime - activeClip.TimelineStart).TotalSeconds / activeClip.FadeIn.TotalSeconds)
                                End If
                                If activeClip.FadeOut > TimeSpan.Zero AndAlso virtTime > activeClip.TimelineEnd - activeClip.FadeOut Then
                                    Dim fadeOutProgress = CSng((activeClip.TimelineEnd - virtTime).TotalSeconds / activeClip.FadeOut.TotalSeconds)
                                    If fadeOutProgress < clipOpacity Then clipOpacity = fadeOutProgress
                                End If

                                Dim drawCmd As New DrawCommand With {
                                    .Frame = frameToDraw,
                                    .Opacity = Math.Max(0.0F, Math.Min(1.0F, clipOpacity)),
                                    .Scale = activeClip.Scale,
                                    .PosX = activeClip.PositionX,
                                    .PosY = activeClip.PositionY,
                                    .Rotation = activeClip.Rotation
                                }

                                framesToDraw.Add(drawCmd)
                                currentPtsSum += frameToDraw.PtsMs
                            End If
                        End If
                    Next

                    If framesToDraw.Count > 0 Then
                        If currentPtsSum <> lastDrawnPts OrElse _forceRedraw Then
                            DrawDirect3D11Frames(framesToDraw, useVSync:=True)
                            lastDrawnPts = currentPtsSum
                            _forceRedraw = False
                        End If

                        For Each item In framesToDraw
                            item.Frame.Dispose()
                        Next
                    End If
                End If

                If currentState = IServices.PlaybackState.Playing Then
                    Thread.SpinWait(10)
                Else
                    Thread.Sleep(10)
                End If
            End While
        Finally
            If isHighResTimerActive Then
                Dim endResult As UInteger = TimeEndPeriod(1)
                If endResult <> TIMERR_NOERROR Then
                    RaiseEvent LogMessage($"Внимание: timeEndPeriod вернул код ошибки {endResult}.")
                End If
            End If

            SyncLock _renderLock
                For Each bmp In _d2dBitmapCache.Values
                    bmp?.Dispose()
                Next
                _d2dBitmapCache.Clear()
            End SyncLock
        End Try
    End Sub

    Private Sub DrawDirect3D11Frames(frames As List(Of DrawCommand), Optional useVSync As Boolean = True)
        SyncLock _renderLock
            If _disposed OrElse frames Is Nothing OrElse frames.Count = 0 Then Return
            Try
                EnsureD3D11Resources()
                If _swapChain Is Nothing OrElse _d2dRenderTarget Is Nothing Then Return

                Dim clientSize As Size = _uiControl.ClientSize
                If clientSize.Width > 0 AndAlso clientSize.Height > 0 AndAlso
                   (_swapChain.Description.ModeDescription.Width <> clientSize.Width OrElse _swapChain.Description.ModeDescription.Height <> clientSize.Height) Then

                    For Each b In _d2dBitmapCache.Values
                        Try
                            If b IsNot Nothing AndAlso Not b.IsDisposed Then b.Dispose()
                        Catch
                        End Try
                    Next
                    _d2dBitmapCache.Clear()

                    _d2dRenderTarget.Dispose()
                    _d2dRenderTarget = Nothing

                    _swapChain.ResizeBuffers(2, clientSize.Width, clientSize.Height, SharpDX.DXGI.Format.B8G8R8A8_UNorm, SwapChainFlags.None)

                    Using backBuffer As Texture2D = _swapChain.GetBackBuffer(Of Texture2D)(0)
                        Using dxgiSurface As DXGI.Surface = backBuffer.QueryInterface(Of DXGI.Surface)()
                            Dim rtp As New D2D1.RenderTargetProperties(New D2D1.PixelFormat(SharpDX.DXGI.Format.B8G8R8A8_UNorm, D2D1.AlphaMode.Premultiplied))
                            _d2dRenderTarget = New D2D1.RenderTarget(_d2dFactory, dxgiSurface, rtp) With {.AntialiasMode = D2D1.AntialiasMode.PerPrimitive}
                        End Using
                    End Using
                End If

                Dim multithread = _d3dDevice.QueryInterface(Of SharpDX.Direct3D11.Multithread)()
                multithread.Enter()
                Try
                    _d2dRenderTarget.BeginDraw()
                    _d2dRenderTarget.Clear(New RawColor4(0, 0, 0, 1.0F))

                    For Each cmd In frames
                        Dim gpuFrame = cmd.Frame.Buffer
                        Dim clipOpacity = cmd.Opacity
                        If gpuFrame Is Nothing Then Continue For

                        Dim framePtr As IntPtr = gpuFrame.Texture.NativePointer
                        Dim bmpToDraw As D2D1.Bitmap = Nothing

                        If Not _d2dBitmapCache.TryGetValue(framePtr, bmpToDraw) OrElse bmpToDraw Is Nothing OrElse bmpToDraw.IsDisposed Then
                            Using surface = gpuFrame.GetSurface()
                                Dim bmpProps As New D2D1.BitmapProperties(New D2D1.PixelFormat(SharpDX.DXGI.Format.B8G8R8A8_UNorm, D2D1.AlphaMode.Premultiplied))
                                bmpToDraw = New D2D1.Bitmap(_d2dRenderTarget, surface, bmpProps)
                                _d2dBitmapCache(framePtr) = bmpToDraw
                            End Using
                        End If

                        Dim ctrlSize = _d2dRenderTarget.Size
                        Dim imgRatio As Single = gpuFrame.Width / CSng(gpuFrame.Height)
                        Dim ctrlRatio As Single = ctrlSize.Width / ctrlSize.Height

                        Dim drawW, drawH, drawX, drawY As Single
                        If imgRatio > ctrlRatio Then
                            drawW = ctrlSize.Width
                            drawH = ctrlSize.Width / imgRatio
                            drawX = 0
                            drawY = (ctrlSize.Height - drawH) / 2.0F
                        Else
                            drawH = ctrlSize.Height
                            drawW = ctrlSize.Height * imgRatio
                            drawX = (ctrlSize.Width - drawW) / 2.0F
                            drawY = 0
                        End If

                        Dim destRect As New RawRectangleF(drawX, drawY, drawX + drawW, drawY + drawH)
                        Dim finalOpacity As Single = _opacity * clipOpacity

                        ' === ТРАНСФОРМАЦИЯ D2D (Scale, Position, Rotation) ===
                        Dim centerX As Single = drawX + (drawW / 2.0F)
                        Dim centerY As Single = drawY + (drawH / 2.0F)

                        Dim rad As Single = CSng(cmd.Rotation * Math.PI / 180.0)
                        Dim c As Single = CSng(Math.Cos(rad)) * cmd.Scale
                        Dim s As Single = CSng(Math.Sin(rad)) * cmd.Scale

                        Dim dx As Single = cmd.PosX + centerX - c * centerX + s * centerY
                        Dim dy As Single = cmd.PosY + centerY - s * centerX - c * centerY

                        _d2dRenderTarget.Transform = New RawMatrix3x2(c, s, -s, c, dx, dy)
                        ' =====================================================

                        _d2dRenderTarget.DrawBitmap(bmpToDraw, destRect, finalOpacity, D2D1.BitmapInterpolationMode.Linear)
                    Next

                    _d2dRenderTarget.Transform = New RawMatrix3x2(1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F)

                    _d2dRenderTarget.EndDraw()

                    If useVSync Then
                        _swapChain.Present(1, PresentFlags.None)
                    Else
                        _swapChain.Present(0, PresentFlags.None)
                    End If
                Finally
                    multithread.Leave()
                    multithread.Dispose()
                End Try

            Catch ex As SharpDXException
                If ex.ResultCode.Code = SharpDX.DXGI.ResultCode.DeviceRemoved.Result.Code OrElse
                   ex.ResultCode.Code = SharpDX.DXGI.ResultCode.DeviceReset.Result.Code Then
                    DiscardD3D11Resources()
                End If
            Catch ex As Exception
                RaiseEvent LogMessage($"Ошибка отрисовки композитинга D3D11: {ex.Message}")
            End Try
        End SyncLock
    End Sub

    Public Sub ShowScrubFrame(frame As GpuVideoFrame, virtualTime As TimeSpan, Optional overrideClip As MediaClip = Nothing)
        If _disposed OrElse frame Is Nothing Then Return

        Dim tempVideoFrame As New VideoFrame With {.Buffer = frame, .PtsMs = frame.PtsMs}

        Dim currentScale As Single = 1.0F
        Dim currentPosX As Single = 0.0F
        Dim currentPosY As Single = 0.0F
        Dim currentRot As Single = 0.0F
        Dim currentOpacity As Single = 1.0F

        ' ИСПРАВЛЕНИЕ: Если инспектор (или рендерер) передает свой измененный клип, используем его!
        If overrideClip IsNot Nothing Then
            currentScale = overrideClip.Scale
            currentPosX = overrideClip.PositionX
            currentPosY = overrideClip.PositionY
            currentRot = overrideClip.Rotation

            If overrideClip.FadeIn > TimeSpan.Zero AndAlso virtualTime < overrideClip.TimelineStart + overrideClip.FadeIn Then
                currentOpacity = CSng((virtualTime - overrideClip.TimelineStart).TotalSeconds / overrideClip.FadeIn.TotalSeconds)
            End If
            If overrideClip.FadeOut > TimeSpan.Zero AndAlso virtualTime > overrideClip.TimelineEnd - overrideClip.FadeOut Then
                Dim fadeOutProgress = CSng((overrideClip.TimelineEnd - virtualTime).TotalSeconds / overrideClip.FadeOut.TotalSeconds)
                If fadeOutProgress < currentOpacity Then currentOpacity = fadeOutProgress
            End If

            ' В противном случае читаем настройки из глобальной модели (стандартное поведение)
        ElseIf ProjectModel IsNot Nothing Then
            Dim activeClips = GetActiveVideoClips(virtualTime)
            If activeClips.Count > 0 Then
                Dim clip = activeClips(0)
                currentScale = clip.Scale
                currentPosX = clip.PositionX
                currentPosY = clip.PositionY
                currentRot = clip.Rotation

                If clip.FadeIn > TimeSpan.Zero AndAlso virtualTime < clip.TimelineStart + clip.FadeIn Then
                    currentOpacity = CSng((virtualTime - clip.TimelineStart).TotalSeconds / clip.FadeIn.TotalSeconds)
                End If
                If clip.FadeOut > TimeSpan.Zero AndAlso virtualTime > clip.TimelineEnd - clip.FadeOut Then
                    Dim fadeOutProgress = CSng((clip.TimelineEnd - virtualTime).TotalSeconds / clip.FadeOut.TotalSeconds)
                    If fadeOutProgress < currentOpacity Then currentOpacity = fadeOutProgress
                End If
            End If
        End If

        Dim list As New List(Of DrawCommand) From {
            New DrawCommand With {
                .Frame = tempVideoFrame,
                .Opacity = Math.Max(0.0F, Math.Min(1.0F, currentOpacity)),
                .Scale = currentScale,
                .PosX = currentPosX,
                .PosY = currentPosY,
                .Rotation = currentRot
            }
        }

        DrawDirect3D11Frames(list, useVSync:=False)

        tempVideoFrame.Buffer = Nothing
        _isScrubbing = True
    End Sub

    Public Sub EndScrubbing()
        _isScrubbing = False
    End Sub

    Private Sub DiscardD3D11Resources()
        For Each bmp In _d2dBitmapCache.Values
            Try
                If bmp IsNot Nothing AndAlso Not bmp.IsDisposed Then bmp.Dispose()
            Catch
            End Try
        Next
        _d2dBitmapCache.Clear()

        If _d2dRenderTarget IsNot Nothing Then
            Try
                _d2dRenderTarget.Dispose()
            Catch
            End Try
            _d2dRenderTarget = Nothing
        End If

        If _d2dFactory IsNot Nothing Then
            Try
                _d2dFactory.Dispose()
            Catch
            End Try
            _d2dFactory = Nothing
        End If

        If _swapChain IsNot Nothing Then
            Try
                _swapChain.Dispose()
            Catch
            End Try
            _swapChain = Nothing
        End If

        If _d3dDevice IsNot Nothing Then
            Try
                _d3dDevice.Dispose()
            Catch
            End Try
            _d3dDevice = Nothing
        End If
    End Sub

    Private Sub PrepareForDispose()
        StopPlayback()
        _renderCts?.Cancel()
        _isScrubbing = False
    End Sub

    Private Sub FinalizeDispose()
        If _renderCts IsNot Nothing Then
            _renderCts.Dispose()
            _renderCts = Nothing
        End If

        SyncLock _streamersLock
            For Each kvp In _streamers
                kvp.Value.Dispose()
            Next
            _streamers.Clear()
        End SyncLock

        SyncLock _renderLock
            DiscardD3D11Resources()
        End SyncLock
    End Sub

    Public Function DisposeAsync() As ValueTask Implements IAsyncDisposable.DisposeAsync
        GC.SuppressFinalize(Me)
        If _disposed Then Return New ValueTask()
        Return New ValueTask(DisposeAsyncCore())
    End Function

    Private Async Function DisposeAsyncCore() As Task
        If Not _disposed Then
            PrepareForDispose()
            If _renderTask IsNot Nothing Then
                Try
                    Await _renderTask.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(False)
                Catch ex As Exception
                End Try
            End If
            FinalizeDispose()
            _disposed = True
        End If
    End Function

    Public Sub Dispose() Implements IDisposable.Dispose
        If Not _disposed Then
            PrepareForDispose()
            If _renderTask IsNot Nothing Then
                Try
                    _renderTask.WaitAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult()
                Catch ex As Exception
                End Try
            End If
            FinalizeDispose()
            _disposed = True
        End If
        GC.SuppressFinalize(Me)
    End Sub

    Private Class VideoStreamer
        Implements IDisposable
        Public ReadOnly Clip As MediaClip
        Private _decoder As AsyncMediaDecoder
        Private _pool As GpuFramePool
        Public ReadOnly Queue As New ConcurrentQueue(Of VideoFrame)()
        Private _cts As CancellationTokenSource
        Private _decodeTask As Task
        Private _d3dDevice As SharpDX.Direct3D11.Device
        Private _context As SharpDX.Direct3D11.DeviceContext
        Private _seekRequest As TimeSpan? = Nothing
        Private ReadOnly _lockObj As New Object()
        Private _isDisposed As Boolean = False

        Public Sub New(clip As MediaClip)
            Me.Clip = clip
        End Sub

        Public Sub Start(d3dDevice As SharpDX.Direct3D11.Device, context As SharpDX.Direct3D11.DeviceContext, startLocalTime As TimeSpan)
            _d3dDevice = d3dDevice
            _context = context
            _cts = New CancellationTokenSource()
            Dim token = _cts.Token

            _decodeTask = Task.Run(Async Function()
                                       Try
                                           _decoder = New AsyncMediaDecoder(Clip.FilePath)
                                           Dim w = _decoder.VideoWidth
                                           Dim h = _decoder.VideoHeight
                                           If w <= 0 Then w = 1920
                                           If h <= 0 Then h = 1080
                                           _pool = New GpuFramePool(_d3dDevice, 10, w, h)

                                           Dim timeToSeek = startLocalTime
                                           Dim performSeek As Boolean = True

                                           While Not token.IsCancellationRequested
                                               Dim pendingSeek As TimeSpan? = Nothing
                                               SyncLock _lockObj
                                                   If _seekRequest.HasValue Then
                                                       pendingSeek = _seekRequest
                                                       _seekRequest = Nothing
                                                   End If
                                               End SyncLock

                                               If pendingSeek.HasValue Then
                                                   timeToSeek = pendingSeek.Value
                                                   performSeek = True
                                                   Dim vf As VideoFrame = Nothing
                                                   While Queue.TryDequeue(vf)
                                                       vf.Dispose()
                                                   End While
                                               End If

                                               If performSeek Then
                                                   performSeek = False
                                                   Dim frame = Await _decoder.ExtractFrameAsync(timeToSeek, _pool, _context, isFastScrub:=True, token)
                                                   If frame IsNot Nothing Then
                                                       Queue.Enqueue(New VideoFrame With {.PtsMs = frame.PtsMs, .Buffer = frame})
                                                   End If
                                                   Continue While
                                               End If

                                               If Queue.Count < 5 Then
                                                   Dim nextF = Await _decoder.ReadNextStreamFrameAsync(_pool, _context, token)
                                                   If nextF IsNot Nothing Then
                                                       Queue.Enqueue(New VideoFrame With {.PtsMs = nextF.PtsMs, .Buffer = nextF})
                                                   Else
                                                       Await Task.Delay(10, token) ' Конец файла
                                                   End If
                                               Else
                                                   Await Task.Delay(5, token)
                                               End If
                                           End While
                                       Catch ex As OperationCanceledException
                                       Catch ex As Exception
                                           Debug.WriteLine($"[VideoStreamer] Ошибка: {ex.Message}")
                                       Finally
                                           If _pool IsNot Nothing Then _pool.DisposeAll()
                                           If _decoder IsNot Nothing Then _decoder.Dispose()
                                       End Try
                                   End Function)
        End Sub

        Public Sub RequestSeek(localTime As TimeSpan)
            SyncLock _lockObj
                _seekRequest = localTime
            End SyncLock
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            If _isDisposed Then Return
            _isDisposed = True
            _cts?.Cancel()
            _cts?.Dispose()
            Dim vf As VideoFrame = Nothing
            While Queue.TryDequeue(vf)
                vf.Dispose()
            End While
        End Sub
    End Class
End Class