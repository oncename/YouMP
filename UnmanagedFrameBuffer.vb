Option Strict On
Option Explicit On

Imports System
Imports System.Runtime.InteropServices

Namespace yoump.Native
    ''' <summary>
    ''' Буфер неуправляемой памяти для хранения сырых пикселей кадра (софтверный фолбэк).
    ''' </summary>
    Public Class UnmanagedFrameBuffer
        Implements IDisposable

        Private _pointer As IntPtr
        Private _disposed As Boolean
        Private ReadOnly _size As Integer
        Private ReadOnly _pitch As Integer

        Public ReadOnly Property Pointer As IntPtr
            Get
                If Not _disposed Then Return _pointer
                Throw New ObjectDisposedException(NameOf(UnmanagedFrameBuffer))
            End Get
        End Property

        Public ReadOnly Property Pitch As Integer
            Get
                Return _pitch
            End Get
        End Property

        Public ReadOnly Property Size As Integer
            Get
                Return _size
            End Get
        End Property

        Public Sub New(width As Integer, height As Integer)
            _pitch = width * 4
            _size = _pitch * height
            _pointer = Marshal.AllocHGlobal(_size)
        End Sub

        Protected Overridable Sub Dispose(disposing As Boolean)
            If Not _disposed Then
                If _pointer <> IntPtr.Zero Then
                    Marshal.FreeHGlobal(_pointer)
                    _pointer = IntPtr.Zero
                End If
                _disposed = True
            End If
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            Dispose(disposing:=True)
            GC.SuppressFinalize(Me)
        End Sub

        Protected Overrides Sub Finalize()
            Dispose(disposing:=False)
        End Sub
    End Class
End Namespace
