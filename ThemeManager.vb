Option Strict On
Option Explicit On

Imports System.Drawing
Imports System.Runtime.InteropServices
Imports System.Windows.Forms
Imports System.IO
Imports System.Text
Imports System.Collections.Generic
Imports System.Reflection

' Аргументы события смены темы
Public Class ThemeChangedEventArgs
    Inherits EventArgs
    Public ReadOnly Property IsDark As Boolean

    Public Sub New(isDark As Boolean)
        Me.IsDark = isDark
    End Sub
End Class

Public Class ThemeManager
    <DllImport("dwmapi.dll", PreserveSig:=True)>
    Private Shared Function DwmSetWindowAttribute(hwnd As IntPtr, attr As Integer, ByRef attrValue As Integer, attrSize As Integer) As Integer
    End Function

    ' API для принудительной перерисовки Non-Client области окна
    <DllImport("user32.dll", SetLastError:=True)>
    Private Shared Function SetWindowPos(hWnd As IntPtr, hWndInsertAfter As IntPtr, x As Integer, y As Integer, cx As Integer, cy As Integer, uFlags As UInteger) As Boolean
    End Function

    ' НОВОЕ: API для нативного переключения темы системных контролов (ComboBox, TextBox, Button и др.)
    <DllImport("uxtheme.dll", CharSet:=CharSet.Unicode)>
    Private Shared Function SetWindowTheme(hWnd As IntPtr, pszSubAppName As String, pszSubIdList As String) As Integer
    End Function

    ' НОВОЕ: API для глубокой мгновенной перерисовки формы и всех дочерних элементов
    <DllImport("user32.dll")>
    Private Shared Function RedrawWindow(hWnd As IntPtr, lprcUpdate As IntPtr, hrgnUpdate As IntPtr, flags As UInteger) As Boolean
    End Function

    Private Const DWMWA_USE_IMMERSIVE_DARK_MODE As Integer = 20
    Private Const DWMWA_WINDOW_CORNER_PREFERENCE As Integer = 33
    Private Const DWMWA_SYSTEMBACKDROP_TYPE As Integer = 38

    Private Const SWP_NOMOVE As UInteger = &H2
    Private Const SWP_NOSIZE As UInteger = &H1
    Private Const SWP_NOZORDER As UInteger = &H4
    Private Const SWP_FRAMECHANGED As UInteger = &H20

    ' Флаги для RedrawWindow
    Public Const RDW_INVALIDATE As UInteger = &H1
    Public Const RDW_IUPDATENOW As UInteger = &H100
    Public Const RDW_ERASE As UInteger = &H4
    Public Const RDW_ALLCHILDREN As UInteger = &H80
    Public Const RDW_FRAME As UInteger = &H400

    ' Глобальный флаг темы
    Public Shared Property IsDarkTheme As Boolean = True

    ' =========================================================================
    ' WEAK EVENT PATTERN ДЛЯ ПРЕДОТВРАЩЕНИЯ УТЕЧЕК ПАМЯТИ И КРАШЕЙ
    ' =========================================================================
    Private Shared ReadOnly _listeners As New List(Of WeakEventHandler)()
    Private Shared ReadOnly _eventLock As New Object()

    ' --- ГЛОБАЛЬНАЯ ШИНА СОБЫТИЙ СО СЛАБЫМИ ССЫЛКАМИ ---
    Public Shared Custom Event ThemeChanged As EventHandler(Of ThemeChangedEventArgs)
        AddHandler(value As EventHandler(Of ThemeChangedEventArgs))
            SyncLock _eventLock
                _listeners.Add(New WeakEventHandler(value))
            End SyncLock
        End AddHandler

        RemoveHandler(value As EventHandler(Of ThemeChangedEventArgs))
            SyncLock _eventLock
                _listeners.RemoveAll(Function(w) w.IsMatch(value))
            End SyncLock
        End RemoveHandler

        RaiseEvent(sender As Object, e As ThemeChangedEventArgs)
            Dim toInvoke As New List(Of EventHandler(Of ThemeChangedEventArgs))()
            Dim toRemove As New List(Of WeakEventHandler)()

            SyncLock _eventLock
                For Each weakHandler In _listeners
                    Dim handler = weakHandler.GetHandler()
                    If handler IsNot Nothing Then
                        toInvoke.Add(handler)
                    Else
                        toRemove.Add(weakHandler)
                    End If
                Next

                For Each deadHandler In toRemove
                    _listeners.Remove(deadHandler)
                Next
            End SyncLock

            For Each handler In toInvoke
                Try
                    handler.Invoke(sender, e)
                Catch ex As ObjectDisposedException
                Catch ex As Exception
                    System.Diagnostics.Debug.WriteLine($"[ThemeManager] Ошибка в обработчике темы: {ex.Message}")
                End Try
            Next
        End RaiseEvent
    End Event

    Private Class WeakEventHandler
        Private ReadOnly _weakTarget As WeakReference
        Private ReadOnly _method As MethodInfo

        Public Sub New(handler As [Delegate])
            If handler.Target IsNot Nothing Then
                _weakTarget = New WeakReference(handler.Target)
            Else
                _weakTarget = Nothing
            End If
            _method = handler.Method
        End Sub

        Public Function GetHandler() As EventHandler(Of ThemeChangedEventArgs)
            If _weakTarget Is Nothing Then
                Return DirectCast([Delegate].CreateDelegate(GetType(EventHandler(Of ThemeChangedEventArgs)), Nothing, _method), EventHandler(Of ThemeChangedEventArgs))
            End If

            Dim target As Object = _weakTarget.Target
            If target IsNot Nothing Then
                Return DirectCast([Delegate].CreateDelegate(GetType(EventHandler(Of ThemeChangedEventArgs)), target, _method), EventHandler(Of ThemeChangedEventArgs))
            End If

            Return Nothing
        End Function

        Public Function IsMatch(handler As [Delegate]) As Boolean
            Dim targetMatch As Boolean

            If _weakTarget Is Nothing Then
                targetMatch = (handler.Target Is Nothing)
            Else
                targetMatch = Object.ReferenceEquals(_weakTarget.Target, handler.Target)
            End If

            Return targetMatch AndAlso _method.Equals(handler.Method)
        End Function
    End Class
    ' =========================================================================

    Public Shared Sub ApplyGlobalTheme(isDark As Boolean)
        If IsDarkTheme = isDark Then Return
        IsDarkTheme = isDark
        RaiseEvent ThemeChanged(Nothing, New ThemeChangedEventArgs(isDark))
    End Sub

    Public Shared Sub LoadSettings()
        Try
            IsDarkTheme = SettingsService.Instance.Current.IsDarkTheme
        Catch ex As Exception
            System.Diagnostics.Debug.WriteLine($"[ThemeManager] Ошибка применения темы из настроек: {ex.Message}")
            IsDarkTheme = True
        End Try
    End Sub

    Public Shared ReadOnly Property BackColor As Color
        Get
            Return If(IsDarkTheme, Color.FromArgb(28, 28, 32), Color.FromArgb(243, 243, 243))
        End Get
    End Property

    Public Shared ReadOnly Property ForeColor As Color
        Get
            Return If(IsDarkTheme, Color.White, Color.FromArgb(20, 20, 20))
        End Get
    End Property

    Public Shared ReadOnly Property PanelBackColor As Color
        Get
            Return If(IsDarkTheme, Color.FromArgb(20, 20, 24), Color.FromArgb(230, 230, 230))
        End Get
    End Property

    Public Shared ReadOnly Property ControlBackColor As Color
        Get
            Return If(IsDarkTheme, Color.FromArgb(45, 45, 48), Color.FromArgb(220, 220, 220))
        End Get
    End Property

    Public Shared ReadOnly Property DimOverlayColor As Color
        Get
            Return If(IsDarkTheme, Color.FromArgb(140, 0, 0, 0), Color.FromArgb(100, 255, 255, 255))
        End Get
    End Property

    Public Shared Sub ApplyDwm(hwnd As IntPtr)
        If hwnd = IntPtr.Zero Then Return

        Try
            Dim hResult As Integer

            Dim useDarkMode As Integer = If(IsDarkTheme, 1, 0)
            hResult = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, useDarkMode, 4)
            If hResult < 0 Then
                System.Diagnostics.Debug.WriteLine($"[ThemeManager] Ошибка DWMWA_USE_IMMERSIVE_DARK_MODE. HRESULT: {hResult}")
            End If

            Dim cornerPreference As Integer = 2
            hResult = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, cornerPreference, 4)
            If hResult < 0 Then
                System.Diagnostics.Debug.WriteLine($"[ThemeManager] Ошибка DWMWA_WINDOW_CORNER_PREFERENCE. HRESULT: {hResult}")
            End If

            Dim backdropType As Integer = 2
            hResult = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, backdropType, 4)
            If hResult < 0 Then
                System.Diagnostics.Debug.WriteLine($"[ThemeManager] Ошибка DWMWA_SYSTEMBACKDROP_TYPE. HRESULT: {hResult}")
            End If

            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE Or SWP_NOSIZE Or SWP_NOZORDER Or SWP_FRAMECHANGED)

        Catch ex As Exception
            System.Diagnostics.Debug.WriteLine($"[ThemeManager] Критическая ошибка вызова DWM API: {ex.Message}")
        End Try
    End Sub

End Class