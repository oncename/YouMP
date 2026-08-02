Option Strict On
Option Explicit On

Imports System.Windows.Forms
Imports System.Drawing

Public Class Form2
    Inherits Form

    Public isFullscreen As Boolean = False
    Public normalBounds As Rectangle
    Public normalWindowState As FormWindowState

    Private Const WM_PAINT As Integer = &HF
    Private Const WM_ERASEBKGND As Integer = &H14

    Public Sub New()
        MyBase.New()
        InitializeComponent()
        Me.DoubleBuffered = True
        Me.SetStyle(ControlStyles.AllPaintingInWmPaint Or ControlStyles.UserPaint Or ControlStyles.OptimizedDoubleBuffer, True)
        Me.UpdateStyles()

        ' ИСПРАВЛЕНИЕ 1: Подписываем окно на глобальное событие смены темы
        AddHandler ThemeManager.ThemeChanged, AddressOf OnThemeChanged
    End Sub

    Private Sub ApplyTheme()
        ThemeManager.ApplyDwm(Me.Handle)
        Me.BackColor = ThemeManager.BackColor
        Me.ForeColor = ThemeManager.ForeColor
        Me.Refresh()
    End Sub

    Private Sub Form2_Load(sender As Object, e As EventArgs) Handles MyBase.Load
        ApplyTheme()
    End Sub

    Private Sub OnThemeChanged(sender As Object, e As ThemeChangedEventArgs)
        If Me.InvokeRequired Then
            Me.BeginInvoke(New Action(Sub() ApplyTheme()))
        Else
            ApplyTheme()
        End If
    End Sub

    Protected Overrides Sub WndProc(ByRef m As Message)
        If m.Msg = WM_ERASEBKGND Then
            m.Result = CType(1, IntPtr)
            Return
        End If
        MyBase.WndProc(m)
    End Sub

    Public Sub ToggleFullscreen()
        If Me.FormBorderStyle = FormBorderStyle.None Then
            Me.FormBorderStyle = FormBorderStyle.Sizable
            Me.WindowState = FormWindowState.Normal
            Me.TopMost = False
            isFullscreen = False
        Else
            Me.FormBorderStyle = FormBorderStyle.None
            Me.WindowState = FormWindowState.Maximized
            Me.TopMost = True
            isFullscreen = True
        End If
    End Sub

    Private Sub Form2_KeyDown(sender As Object, e As KeyEventArgs) Handles Me.KeyDown
        If e.KeyCode = Keys.Escape AndAlso isFullscreen Then
            ToggleFullscreen()
        End If
    End Sub

    Private Sub Form2_MouseDoubleClick(sender As Object, e As MouseEventArgs) Handles Me.MouseDoubleClick
        If e.Button = MouseButtons.Left Then
            ToggleFullscreen()
        End If
    End Sub

    ' Отписываемся от события при закрытии формы, чтобы избежать утечек памяти
    Private Sub Form2_FormClosing(sender As Object, e As FormClosingEventArgs) Handles MyBase.FormClosing
        RemoveHandler ThemeManager.ThemeChanged, AddressOf OnThemeChanged
    End Sub
End Class