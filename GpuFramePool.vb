Imports System.Collections.Concurrent
Imports SharpDX.Direct3D11

Public Class GpuFramePool
    Private ReadOnly _device As Device

    ' Свободные кадры, готовые к выдаче
    Private ReadOnly _availableFrames As New ConcurrentBag(Of GpuVideoFrame)()

    ' ИСПРАВЛЕНИЕ: Мастер-список ВСЕХ созданных кадров для жесткой защиты от утечек VRAM
    Private ReadOnly _allAllocatedFrames As New ConcurrentBag(Of GpuVideoFrame)()

    Private ReadOnly _maxSize As Integer
    Public ReadOnly Width As Integer
    Public ReadOnly Height As Integer
    Private _isDisposed As Boolean = False

    Public Sub New(device As Device, maxSize As Integer, width As Integer, height As Integer)
        _device = device
        _maxSize = maxSize
        Me.Width = width
        Me.Height = height
        Preallocate()
    End Sub

    Private Sub Preallocate()
        For i As Integer = 0 To _maxSize - 1
            Dim frame As New GpuVideoFrame(_device, Width, Height, Me)
            _availableFrames.Add(frame)
            _allAllocatedFrames.Add(frame)
        Next
    End Sub

    Public Function Rent() As GpuVideoFrame
        Dim frame As GpuVideoFrame = Nothing
        If _availableFrames.TryTake(frame) Then
            frame.ResetState()
            Return frame
        End If

        ' Если пул пуст (потребовалось больше кадров), создаем новый, 
        ' но обязательно фиксируем его в мастер-списке!
        Dim newFrame As New GpuVideoFrame(_device, Width, Height, Me)
        _allAllocatedFrames.Add(newFrame)
        Return newFrame
    End Function

    Public Sub ReturnFrame(frame As GpuVideoFrame)
        If frame Is Nothing Then Return

        If _isDisposed Then
            ' Если пул уже уничтожен, принудительно выгружаем текстуру из VRAM
            frame.RealDispose()
        Else
            _availableFrames.Add(frame)
        End If
    End Sub

    Public Sub DisposeAll()
        _isDisposed = True

        ' ИСПРАВЛЕНИЕ: Уничтожаем ВСЕ кадры напрямую из VRAM, 
        ' даже те, которые зависли в UI и не были возвращены через ReturnFrame
        For Each frame In _allAllocatedFrames
            Try
                frame.RealDispose()
            Catch : End Try
        Next

        ' Очищаем коллекции (исправленный синтаксис VB.NET)
        Dim dummy As GpuVideoFrame = Nothing
        While _availableFrames.TryTake(dummy)
        End While
    End Sub
End Class