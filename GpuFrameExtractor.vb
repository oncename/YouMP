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

Public Class GpuFrameExtractor
    Implements IDisposable

    Private ReadOnly _cache As GpuFrameCacheManager
    Private _decoder As NativeMediaDecoder
    Private ReadOnly _device As Device
    Private ReadOnly _context As DeviceContext
    Private _semaphore As New SemaphoreSlim(1, 1)
    Private _cancellationTokenSource As New CancellationTokenSource()
    Private _disposed As Boolean

    Private _videoDevice As VideoDevice
    Private _videoContext As VideoContext
    Private _vpEnum As VideoProcessorEnumerator
    Private _vProcessor As VideoProcessor

    Private _lastSrcW, _lastSrcH, _lastDstW, _lastDstH As Integer

    Private ReadOnly _outputViews As New Dictionary(Of IntPtr, VideoProcessorOutputView)()
    Private ReadOnly _disposeLock As New Object()

    Public Event LogMessage As Action(Of String)

    Public Sub New(cache As GpuFrameCacheManager, videoPath As String, device As Device, context As DeviceContext)
        _cache = cache
        _device = device
        _context = context
        _decoder = New NativeMediaDecoder(videoPath, device.NativePointer)
    End Sub

    Private Sub EnsureVideoProcessor(srcW As Integer, srcH As Integer, dstW As Integer, dstH As Integer)
        If _device Is Nothing OrElse _context Is Nothing Then Return

        If _vProcessor IsNot Nothing Then
            If _lastSrcW = srcW AndAlso _lastSrcH = srcH AndAlso _lastDstW = dstW AndAlso _lastDstH = dstH Then Return

            For Each view In _outputViews.Values
                view?.Dispose()
            Next
            _outputViews.Clear()

            _vProcessor?.Dispose()
            _vpEnum?.Dispose()
        End If

        _lastSrcW = srcW : _lastSrcH = srcH
        _lastDstW = dstW : _lastDstH = dstH

        Try
            _videoDevice = _device.QueryInterface(Of VideoDevice)()
            _videoContext = _context.QueryInterface(Of VideoContext)()

            If _videoDevice Is Nothing OrElse _videoContext Is Nothing Then Return

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
        Catch ex As Exception
            ' Игнорируем ошибки DXGI, если устройство было потеряно
        End Try
    End Sub

    Private Sub ConvertNv12ToBgraOnGpu(sourceTex As Texture2D, srcSubresource As Integer, destFrame As GpuVideoFrame)
        ' ЖЕСТКАЯ ЗАЩИТА: Проверяем, что все COM-объекты живы
        If _videoDevice Is Nothing OrElse _videoContext Is Nothing OrElse _vProcessor Is Nothing OrElse _vpEnum Is Nothing Then Return
        If destFrame Is Nothing OrElse destFrame.Texture Is Nothing Then Return

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

            SyncLock _disposeLock
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
        Catch ex As Exception
            ' Подавляем краши видеопроцессора
        Finally
            inView?.Dispose()
        End Try
    End Sub

    Public Async Function EnsureFrameCachedAsync(index As Integer, Optional token As CancellationToken = Nothing) As Task
        SyncLock _disposeLock
            If _disposed OrElse _decoder Is Nothing Then Return
        End SyncLock

        ' ЗАЩИТА: Проверяем доступность кэша и пула ДО начала работы
        If _cache Is Nothing OrElse _cache.Pool Is Nothing Then Return

        If index < 0 OrElse index >= _cache.TotalSlots Then Return
        If _cache.IsFrameCached(index) Then Return

        Dim linkedToken As CancellationToken
        Try
            Dim linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cancellationTokenSource.Token, token)
            linkedToken = linkedCts.Token
        Catch ex As Exception
            Return
        End Try

        Try
            Await _semaphore.WaitAsync(linkedToken).ConfigureAwait(False)
        Catch ex As OperationCanceledException
            Return
        Catch ex As ObjectDisposedException
            Return
        End Try

        Try
            SyncLock _disposeLock
                If _disposed Then Return
            End SyncLock

            If _cache.IsFrameCached(index) Then Return

            linkedToken.ThrowIfCancellationRequested()

            Dim targetTime = _cache.GetTimeForSlot(index)
            Dim gpuFrame As GpuVideoFrame = Nothing
            Dim committed As Boolean = False

            ' Запоминаем пул локально, чтобы он не пропал на полпути
            Dim localPool As GpuFramePool = _cache.Pool
            If localPool Is Nothing Then Return

            Try
                ' Арендуем кадр вне Task.Run
                Try
                    gpuFrame = localPool.Rent()
                Catch
                    Return ' Ошибка аренды (пул уничтожен)
                End Try

                If gpuFrame Is Nothing OrElse gpuFrame.Texture Is Nothing Then Return

                Dim success = Await Task.Run(Function() As Boolean
                                                 Dim localDecoder As NativeMediaDecoder = Nothing

                                                 SyncLock _disposeLock
                                                     If _disposed Then Return False
                                                     localDecoder = _decoder
                                                 End SyncLock

                                                 If localDecoder Is Nothing Then Return False

                                                 linkedToken.ThrowIfCancellationRequested()

                                                 Dim w As Integer = localPool.Width
                                                 Dim h As Integer = localPool.Height
                                                 Dim ptsMs As Double = 0

                                                 Try
                                                     localDecoder.Seek(targetTime, False)
                                                 Catch
                                                     Return False
                                                 End Try

                                                 Dim pSourceTex As IntPtr = IntPtr.Zero
                                                 Dim srcSubresource As Integer = 0

                                                 ' 1. Аппаратный кадр (Zero Copy)
                                                 If localDecoder.TryReadNextHardwareFrame(pSourceTex, srcSubresource, ptsMs) Then
                                                     If pSourceTex <> IntPtr.Zero Then
                                                         Try
                                                             Marshal.AddRef(pSourceTex)
                                                             ' Нативная блокировка конвейера D3D11
                                                             Using multithread = _device.QueryInterface(Of SharpDX.Direct3D11.Multithread)()
                                                                 multithread.Enter()
                                                                 Try
                                                                     Using sourceTex As New Texture2D(pSourceTex)
                                                                         EnsureVideoProcessor(sourceTex.Description.Width, sourceTex.Description.Height, w, h)
                                                                         ConvertNv12ToBgraOnGpu(sourceTex, srcSubresource, gpuFrame)
                                                                     End Using
                                                                 Finally
                                                                     multithread.Leave()
                                                                 End Try
                                                             End Using

                                                             gpuFrame.PtsMs = ptsMs
                                                             Return True
                                                         Catch ex As Exception
                                                             Return False
                                                         End Try
                                                     End If
                                                 End If

                                                 ' 2. Программный фолбэк
                                                 Try
                                                     Using ufb As New yoump.Native.UnmanagedFrameBuffer(w, h)
                                                         If localDecoder.TryReadNextFrame(ufb.Pointer, w, h, ufb.Pitch, ptsMs) Then
                                                             ' Нативная блокировка конвейера D3D11
                                                             Using multithread = _device.QueryInterface(Of SharpDX.Direct3D11.Multithread)()
                                                                 multithread.Enter()
                                                                 Try
                                                                     gpuFrame.CopyFromSystemMemory(_context, ufb.Pointer, ufb.Pitch)
                                                                 Finally
                                                                     multithread.Leave()
                                                                 End Try
                                                             End Using

                                                             gpuFrame.PtsMs = ptsMs
                                                             Return True
                                                         End If
                                                     End Using
                                                 Catch
                                                     Return False
                                                 End Try

                                                 Return False
                                             End Function, linkedToken).ConfigureAwait(False)

                If success AndAlso Not linkedToken.IsCancellationRequested Then
                    _cache.CommitFrame(index, gpuFrame)
                    committed = True
                End If
            Finally
                If Not committed AndAlso gpuFrame IsNot Nothing Then
                    gpuFrame.Dispose()
                End If
            End Try

        Catch ex As OperationCanceledException
        Catch ex As Exception
            ' Подавляем лог, если класс уже был очищен (штатное завершение)
            If Not _disposed Then
                RaiseEvent LogMessage($"GpuFrameExtractor error: {ex.Message}")
            End If
        Finally
            SyncLock _disposeLock
                If Not _disposed AndAlso _semaphore IsNot Nothing Then
                    Try
                        _semaphore.Release()
                    Catch ex As ObjectDisposedException
                    End Try
                End If
            End SyncLock
        End Try
    End Function

    Public Sub CancelAll()
        SyncLock _disposeLock
            If _disposed Then Return
            Try
                Dim oldCts = Interlocked.Exchange(_cancellationTokenSource, New CancellationTokenSource())
                If oldCts IsNot Nothing Then
                    oldCts.Cancel()
                    oldCts.Dispose()
                End If
            Catch
            End Try
        End SyncLock
    End Sub

    Protected Overridable Sub Dispose(disposing As Boolean)
        SyncLock _disposeLock
            If Not _disposed Then
                If disposing Then
                    Try
                        If _cancellationTokenSource IsNot Nothing Then
                            If Not _cancellationTokenSource.IsCancellationRequested Then
                                _cancellationTokenSource.Cancel()
                            End If
                            _cancellationTokenSource.Dispose()
                            _cancellationTokenSource = Nothing
                        End If
                    Catch
                    End Try

                    _decoder?.Dispose()
                    _decoder = Nothing

                    For Each view In _outputViews.Values
                        view?.Dispose()
                    Next
                    _outputViews.Clear()

                    _vProcessor?.Dispose()
                    _vProcessor = Nothing

                    _vpEnum?.Dispose()
                    _vpEnum = Nothing

                    _videoContext?.Dispose()
                    _videoContext = Nothing

                    _videoDevice?.Dispose()
                    _videoDevice = Nothing

                    _semaphore?.Dispose()
                    _semaphore = Nothing
                End If
                _disposed = True
            End If
        End SyncLock
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        Dispose(disposing:=True)
        GC.SuppressFinalize(Me)
    End Sub
End Class