Option Strict On
Option Explicit On

Imports System

''' <summary>
''' Неаллоцирующий стек фиксированного размера на базе кольцевого буфера.
''' Идеально подходит для отслеживания истории зума без давления на сборщик мусора.
''' </summary>
Public Class CircularStack(Of T)
    Private ReadOnly _buffer() As T
    Private _head As Integer = 0
    Private _count As Integer = 0
    Private ReadOnly _capacity As Integer

    Public Sub New(capacity As Integer)
        If capacity <= 0 Then Throw New ArgumentException("Емкость должна быть больше нуля.", NameOf(capacity))
        _capacity = capacity
        ReDim _buffer(capacity - 1)
    End Sub

    Public ReadOnly Property Count As Integer
        Get
            Return _count
        End Get
    End Property

    Public Sub Push(item As T)
        _buffer(_head) = item
        _head = (_head + 1) Mod _capacity

        If _count < _capacity Then
            _count += 1
        End If
    End Sub

    Public Function Pop() As T
        If _count = 0 Then Throw New InvalidOperationException("Стек пуст.")

        _head = (_head - 1 + _capacity) Mod _capacity
        Dim item As T = _buffer(_head)

        ' Обязательно зануляем ссылку, чтобы GC мог немедленно собрать объект
        _buffer(_head) = Nothing
        _count -= 1

        Return item
    End Function

    Public Sub Clear()
        Array.Clear(_buffer, 0, _capacity)
        _head = 0
        _count = 0
    End Sub
End Class