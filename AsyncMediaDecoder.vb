Option Strict On
Option Explicit On

Imports System
Imports System.Collections.Generic
Imports System.Runtime.InteropServices
Imports System.Threading
Imports System.Threading.Tasks
Imports SharpDX.Direct3D11
Imports SharpDX.Mathematics.Interop
Imports yoump.Native

Public Class AsyncMediaDecoder
    Implements IDisposable

    Private ReadOnly _filePath As String
    Private ReadOnly _decoderLock As New SemaphoreSlim(1, 1)
    Private _disposed As Boolean = False
    Private _decoder As NativeMediaDecoder

    Private _videoDevice As VideoDevice
    Private _videoContext As VideoContext
    Private _vpEnum As VideoProcessorEnumerator
    Private _vProcessor As VideoProcessor

    Private _lastSrcW, _lastSrcH, _lastDstW, _lastDstH As Integer

    Private ReadOnly _outputViews As New Dictionary(Of IntPtr, VideoProcessorOutputView)()
    Private ReadOnly _viewsLock As New Object()

    Public ReadOnly Property VideoWidth As Integer
        Get
            EnsureDecoder(Nothing)
            Return If(_disposed OrElse _decoder Is Nothing, 0, _decoder.VideoWidth)
        End Get
    End Property

    Public ReadOnly Property VideoHeight As Integer
        Get
            EnsureDecoder(Nothing)
            Return If(_disposed OrElse _decoder Is Nothing, 0, _decoder.VideoHeight)
        End Get
    End Property

    Public Sub New(filePath As String)
        _filePath = filePath
    End Sub

    Private Sub EnsureDecoder(device As Device)
        If _decoder Is Nothing AndAlso Not _disposed Then
            SyncLock _viewsLock
                For Each view In _outputViews.Values
                    view.Dispose()
                Next
                _outputViews.Clear()
            End SyncLock

            Dim ptr As IntPtr = If(device IsNot Nothing, device.NativePointer, IntPtr.Zero)
            _decoder = New NativeMediaDecoder(_filePath, ptr)
        End If
    End Sub

    Private Sub EnsureVideoProcessor(device As Device, context As DeviceContext, srcW As Integer, srcH As Integer, dstW As Integer, dstH As Integer)
        If _vProcessor IsNot Nothing Then
            If _lastSrcW = srcW AndAlso _lastSrcH = srcH AndAlso _lastDstW = dstW AndAlso _lastDstH = dstH Then
                Return
            End If

            SyncLock _viewsLock
                For Each view In _outputViews.Values : view.Dispose() : Next
                _outputViews.Clear()
            End SyncLock

            _vProcessor.Dispose()
            _vProcessor = Nothing

            _vpEnum.Dispose()
            _vpEnum = Nothing

            ' ИСПРАВЛЕНИЕ: Предотвращаем утечку COM-объектов (VRAM)
            ' Освобождаем старые интерфейсы перед вызовом нового QueryInterface
            If _videoContext IsNot Nothing Then
                _videoContext.Dispose()
                _videoContext = Nothing
            End If

            If _videoDevice IsNot Nothing Then
                _videoDevice.Dispose()
                _videoDevice = Nothing
            End If
        End If

        _lastSrcW = srcW : _lastSrcH = srcH
        _lastDstW = dstW : _lastDstH = dstH

        ' Запрашиваем новые COM-интерфейсы
        _videoDevice = device.QueryInterface(Of VideoDevice)()
        _videoContext = context.QueryInterface(Of VideoContext)()

        Dim contentDesc As New VideoProcessorContentDescription With {
            .InputFrameFormat = VideoFrameFormat.Progressive,
            .InputWidth = srcW,
            .InputHeight = srcH,
            .OutputWidth = dstW,
            .OutputHeight = dstH,
            .Usage = VideoUsage.PlaybackNormal
        }

        _videoDevice.CreateVideoProcessorEnumerator(contentDesc, _vpEnum)
        _videoDevice.CreateVideoProcessor(_vpEnum, 0, _vProcessor)

        Dim srcRect As New RawRectangle(0, 0, srcW, srcH)
        Dim dstRect As New RawRectangle(0, 0, dstW, dstH)
        _videoContext.VideoProcessorSetStreamSourceRect(_vProcessor, 0, True, srcRect)
        _videoContext.VideoProcessorSetStreamDestRect(_vProcessor, 0, True, dstRect)
        _videoContext.VideoProcessorSetOutputTargetRect(_vProcessor, True, dstRect)
    End Sub

    Private Sub ConvertNv12ToBgraOnGpu(sourceTex As Texture2D, srcSubresource As Integer, destFrame As GpuVideoFrame)
        Dim destPtr = destFrame.Texture.NativePointer
        Dim inView As VideoProcessorInputView = Nothing
        Dim outView As VideoProcessorOutputView = Nothing

        Try
            Dim inViewDesc As New VideoProcessorInputViewDescription With {
                .FourCC = 0,
                .Dimension = VpivDimension.Texture2D,
                .Texture2D = New Texture2DVpiv With {
                    .ArraySlice = srcSubresource,
                    .MipSlice = 0
                }
            }
            _videoDevice.CreateVideoProcessorInputView(sourceTex, _vpEnum, inViewDesc, inView)

            SyncLock _viewsLock
                If Not _outputViews.TryGetValue(destPtr, outView) Then
                    Dim outViewDesc As New VideoProcessorOutputViewDescription With {
                        .Dimension = VpovDimension.Texture2D,
                        .Texture2D = New Texture2DVpov With {
                            .MipSlice = 0
                        }
                    }
                    _videoDevice.CreateVideoProcessorOutputView(destFrame.Texture, _vpEnum, outViewDesc, outView)
                    _outputViews(destPtr) = outView
                End If
            End SyncLock

            Dim streamData As New VideoProcessorStream With {
                .Enable = True,
                .OutputIndex = 0,
                .InputFrameOrField = 0,
                .PastFrames = 0,
                .FutureFrames = 0,
                .PInputSurface = inView
            }

            _videoContext.VideoProcessorBlt(_vProcessor, outView, 0, 1, New VideoProcessorStream() {streamData})

        Finally
            inView?.Dispose()
        End Try
    End Sub

    Public Async Function ExtractFrameAsync(
        timePosition As TimeSpan,
        pool As GpuFramePool,
        context As DeviceContext,
        token As CancellationToken) As Task(Of GpuVideoFrame)

        Return Await GetFrameInternalAsync(True, timePosition, pool, context, False, token)
    End Function

    Public Async Function ExtractFrameAsync(
        timePosition As TimeSpan,
        pool As GpuFramePool,
        context As DeviceContext,
        isFastScrub As Boolean,
        token As CancellationToken) As Task(Of GpuVideoFrame)

        Return Await GetFrameInternalAsync(True, timePosition, pool, context, isFastScrub, token)
    End Function

    Public Async Function ReadNextStreamFrameAsync(
        pool As GpuFramePool,
        context As DeviceContext,
        token As CancellationToken) As Task(Of GpuVideoFrame)

        Return Await GetFrameInternalAsync(False, TimeSpan.Zero, pool, context, False, token)
    End Function

    Private Async Function GetFrameInternalAsync(
        doSeek As Boolean,
        timePosition As TimeSpan,
        pool As GpuFramePool,
        context As DeviceContext,
        isFastScrub As Boolean,
        token As CancellationToken) As Task(Of GpuVideoFrame)

        ObjectDisposedException.ThrowIf(_disposed, Me)
        If pool Is Nothing OrElse context Is Nothing Then Return Nothing

        Dim lockAcquired As Boolean = False

        Try
            Await _decoderLock.WaitAsync(token).ConfigureAwait(False)
            lockAcquired = True

            Return Await Task.Run(Function() As GpuVideoFrame
                                      If _disposed Then Return Nothing
                                      token.ThrowIfCancellationRequested()

                                      Dim device = context.Device
                                      EnsureDecoder(device)

                                      Dim frame As GpuVideoFrame = Nothing
                                      Dim frameReturned As Boolean = False

                                      Try
                                          frame = pool.Rent()

                                          If doSeek Then
                                              _decoder.Seek(timePosition, isFastScrub)
                                          End If

                                          Dim pSourceTex As IntPtr = IntPtr.Zero
                                          Dim srcSubresource As Integer = 0
                                          Dim ptsMs As Double = 0

                                          ' 1. Пытаемся получить аппаратный кадр (Zero-Copy)
                                          If _decoder.TryReadNextHardwareFrame(pSourceTex, srcSubresource, ptsMs) Then
                                              If pSourceTex <> IntPtr.Zero Then
                                                  Dim hwTexture As Texture2D = Nothing
                                                  Try
                                                      Marshal.AddRef(pSourceTex)
                                                      ' НАСТОЯЩАЯ БЛОКИРОВКА КОНВЕЙЕРА D3D11
                                                      Using multithread = device.QueryInterface(Of SharpDX.Direct3D11.Multithread)()
                                                          multithread.Enter()
                                                          Try
                                                              hwTexture = New Texture2D(pSourceTex)
                                                              EnsureVideoProcessor(device, context, hwTexture.Description.Width, hwTexture.Description.Height, frame.Width, frame.Height)
                                                              ConvertNv12ToBgraOnGpu(hwTexture, srcSubresource, frame)
                                                          Finally
                                                              multithread.Leave()
                                                          End Try
                                                      End Using

                                                      frame.PtsMs = ptsMs
                                                      frameReturned = True
                                                      Return frame
                                                  Catch ex As Exception
                                                      System.Diagnostics.Debug.WriteLine($"[AsyncMediaDecoder] Ошибка аппаратного кадра: {ex.Message}")
                                                  Finally
                                                      hwTexture?.Dispose()
                                                  End Try
                                              End If
                                          End If

                                          ' 2. Если GPU не справился, используем программный фолбэк через ОЗУ
                                          Using ufb As New yoump.Native.UnmanagedFrameBuffer(pool.Width, pool.Height)
                                              If _decoder.TryReadNextFrame(ufb.Pointer, pool.Width, pool.Height, ufb.Pitch, ptsMs) Then
                                                  ' НАСТОЯЩАЯ БЛОКИРОВКА КОНВЕЙЕРА D3D11
                                                  Using multithread = device.QueryInterface(Of SharpDX.Direct3D11.Multithread)()
                                                      multithread.Enter()
                                                      Try
                                                          frame.CopyFromSystemMemory(context, ufb.Pointer, ufb.Pitch)
                                                      Finally
                                                          multithread.Leave()
                                                      End Try
                                                  End Using

                                                  frame.PtsMs = ptsMs
                                                  frameReturned = True
                                                  Return frame
                                              End If
                                          End Using

                                          Return Nothing

                                      Finally
                                          If Not frameReturned AndAlso frame IsNot Nothing Then
                                              frame.Dispose()
                                          End If
                                      End Try
                                  End Function, token).ConfigureAwait(False)

        Catch ex As OperationCanceledException
            Return Nothing
        Catch ex As Exception
            Return Nothing
        Finally
            If lockAcquired Then _decoderLock.Release()
        End Try
    End Function

    Private Sub ReleaseResources()
        Try
            _decoder?.Dispose()
            _decoder = Nothing

            SyncLock _viewsLock
                For Each view In _outputViews.Values
                    view.Dispose()
                Next
                _outputViews.Clear()
            End SyncLock

            _vProcessor?.Dispose()
            _vProcessor = Nothing

            _vpEnum?.Dispose()
            _vpEnum = Nothing

            _videoContext?.Dispose()
            _videoContext = Nothing

            _videoDevice?.Dispose()
            _videoDevice = Nothing
        Catch ex As Exception
        End Try
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        If Not _disposed Then
            _disposed = True

            ' Если блокировка свободна, очищаем сразу
            Dim lockAcquired As Boolean = False
            Try
                lockAcquired = _decoderLock.Wait(0)
            Catch
            End Try

            If lockAcquired Then
                Try
                    ReleaseResources()
                Finally
                    _decoderLock.Release()
                    _decoderLock.Dispose()
                End Try
            Else
                ' Запускаем очистку в фоновом потоке, чтобы не блокировать UI.
                ' ИСПРАВЛЕНИЕ: Добавлен жесткий таймаут 2000 мс.
                ' Если поток внутри FFmpeg завис из-за I/O или битого кадра, мы
                ' прекращаем ожидание и не уничтожаем нативные ресурсы, 
                ' чтобы избежать падения приложения (ExecutionEngineException).
                Task.Run(Sub()
                             Try
                                 If _decoderLock.Wait(2000) Then
                                     Try
                                         ReleaseResources()
                                     Finally
                                         _decoderLock.Release()
                                         _decoderLock.Dispose()
                                     End Try
                                 Else
                                     System.Diagnostics.Debug.WriteLine("[AsyncMediaDecoder] Таймаут освобождения: FFmpeg завис, очистка прервана для избежания краша.")
                                 End If
                             Catch
                             End Try
                         End Sub)
            End If
        End If
        GC.SuppressFinalize(Me)
    End Sub
End Class