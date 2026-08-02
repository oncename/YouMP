Option Strict On
Option Explicit On

Imports System
Imports System.Threading
Imports SharpDX
Imports SharpDX.Direct3D11

Public Class GpuVideoFrame
    Implements IDisposable

    Public ReadOnly Texture As Texture2D
    Public ReadOnly ShaderResourceView As ShaderResourceView
    Public ReadOnly Width As Integer
    Public ReadOnly Height As Integer
    Public PtsMs As Double

    Private ReadOnly _pool As GpuFramePool
    Private ReadOnly _device As Device

    ' 0 = Свободен/Активен, 1 = Находится в пуле, 2 = Полностью уничтожен (Disposed)
    Private _state As Integer = 0
    Private _forceRealDispose As Boolean = False

    Public Sub New(device As SharpDX.Direct3D11.Device, width As Integer, height As Integer, pool As GpuFramePool)
        Me.Width = width
        Me.Height = height
        _pool = pool
        _device = device

        Dim texDesc As New Texture2DDescription() With {
            .Width = width,
            .Height = height,
            .MipLevels = 1,
            .ArraySize = 1,
            .Format = SharpDX.DXGI.Format.B8G8R8A8_UNorm,
            .SampleDescription = New SharpDX.DXGI.SampleDescription(1, 0),
            .Usage = ResourceUsage.Default,
            .BindFlags = BindFlags.ShaderResource Or BindFlags.RenderTarget,
            .CpuAccessFlags = CpuAccessFlags.None,
            .OptionFlags = ResourceOptionFlags.None
        }
        Texture = New Texture2D(device, texDesc)
        ShaderResourceView = New ShaderResourceView(device, Texture)
    End Sub

    Public Sub CopyFromSystemMemory(context As DeviceContext, dataPtr As IntPtr, pitch As Integer)
        If dataPtr = IntPtr.Zero OrElse Volatile.Read(_state) = 2 Then Return
        Dim box As New DataBox(dataPtr, pitch, 0)
        context.UpdateSubresource(box, Texture, 0)
    End Sub

    Public Function GetSurface() As SharpDX.DXGI.Surface
        If Volatile.Read(_state) = 2 Then Return Nothing
        Return Texture.QueryInterface(Of SharpDX.DXGI.Surface)()
    End Function

    ''' <summary>
    ''' Сбрасывает состояние кадра при выдаче из пула.
    ''' </summary>
    Friend Sub ResetState()
        Interlocked.Exchange(_state, 0)
    End Sub

    ''' <summary>
    ''' Принудительно выгружает текстуру из видеопамяти, минуя пул.
    ''' </summary>
    Public Sub RealDispose()
        _forceRealDispose = True
        Me.Dispose()
    End Sub

    ''' <summary>
    ''' Стандартный Dispose возвращает кадр в пул.
    ''' </summary>
    Public Sub Dispose() Implements IDisposable.Dispose
        ' Если объект уже уничтожен (2), ничего не делаем.
        If Volatile.Read(_state) = 2 Then Return

        If Not _forceRealDispose AndAlso _pool IsNot Nothing Then
            ' Если кадр еще не в пуле (0), возвращаем его (1)
            If Interlocked.CompareExchange(_state, 1, 0) = 0 Then
                _pool.ReturnFrame(Me)
                ' ИСПРАВЛЕНИЕ: Выходим только если кадр успешно вернулся в пул
                Return
            End If
            ' Если мы оказались здесь, значит кадр не был в состоянии 0. 
            ' Позволяем коду пойти дальше и выполнить аппаратный Dispose (защита от двойного вызова и зависания).
        End If

        ' Фактическое уничтожение ресурсов DirectX в текущем потоке.
        ' Меняем состояние на "2" (Уничтожен). Если уже было 2, выходим.
        If Interlocked.Exchange(_state, 2) = 2 Then Return

        If ShaderResourceView IsNot Nothing AndAlso Not ShaderResourceView.IsDisposed Then
            Try
                ShaderResourceView.Dispose()
            Catch : End Try
        End If

        If Texture IsNot Nothing AndAlso Not Texture.IsDisposed Then
            Try
                Texture.Dispose()
            Catch : End Try
        End If

        GC.SuppressFinalize(Me)
    End Sub

    ' ВНИМАНИЕ: Финализатор (Protected Overrides Sub Finalize) УДАЛЕН НАМЕРЕННО!
    ' SharpDX падает с ExecutionEngineException, если вызывать Marshal.Release()
    ' из фонового потока сборщика мусора. Память VRAM должна управляться пулом.
End Class