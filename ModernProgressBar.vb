Imports System.Windows.Forms
Imports System.Drawing
Imports System.ComponentModel

Public Class ModernProgressBar
    Inherits Control

    Private _value As Integer = 0
    Private _minimum As Integer = 0
    Private _maximum As Integer = 100

    <Browsable(True)>
    <Category("Behavior")>
    <DefaultValue(0)>
    <Description("Текущее значение прогресс-бара.")>
    <DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)>
    Public Property Value As Integer
        Get
            Return _value
        End Get
        Set(v As Integer)
            ' ИСПРАВЛЕНИЕ: Безопасное ограничение Value рамками Minimum и Maximum
            _value = Math.Max(_minimum, Math.Min(_maximum, v))
            Me.Invalidate()
        End Set
    End Property

    <Browsable(True)>
    <Category("Behavior")>
    <DefaultValue(0)>
    <Description("Минимальное значение прогресс-бара.")>
    <DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)>
    Public Property Minimum As Integer
        Get
            Return _minimum
        End Get
        Set(v As Integer)
            ' ИСПРАВЛЕНИЕ: Гарантируем, что Minimum не превысит Maximum, и корректируем Value
            If v > _maximum Then _maximum = v
            _minimum = v
            If _value < _minimum Then _value = _minimum
            Me.Invalidate()
        End Set
    End Property

    <Browsable(True)>
    <Category("Behavior")>
    <DefaultValue(100)>
    <Description("Максимальное значение прогресс-бара.")>
    <DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)>
    Public Property Maximum As Integer
        Get
            Return _maximum
        End Get
        Set(v As Integer)
            ' ИСПРАВЛЕНИЕ: Гарантируем, что Maximum не станет меньше Minimum, и корректируем Value
            If v < _minimum Then _minimum = v
            _maximum = v
            If _value > _maximum Then _value = _maximum
            Me.Invalidate()
        End Set
    End Property

    Public Sub New()
        Me.DoubleBuffered = True
        Me.SetStyle(ControlStyles.AllPaintingInWmPaint Or ControlStyles.UserPaint Or ControlStyles.OptimizedDoubleBuffer, True)
    End Sub

    Protected Overrides Sub OnPaint(e As PaintEventArgs)
        MyBase.OnPaint(e)
        Dim g = e.Graphics
        g.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias

        ' 1. Фон полосы (из ThemeManager)
        Using bgBrush As New SolidBrush(ThemeManager.ControlBackColor)
            g.FillRectangle(bgBrush, Me.ClientRectangle)
        End Using

        ' 2. Заливка прогресса
        If _value > _minimum AndAlso _maximum > _minimum Then
            Dim percent As Double = (_value - _minimum) / CDbl(_maximum - _minimum)
            Dim fillWidth As Integer = CInt(percent * Me.Width)
            Dim fillRect = New Rectangle(0, 0, fillWidth, Me.Height)

            Using fillBrush As New SolidBrush(Color.FromArgb(0, 120, 215))
                g.FillRectangle(fillBrush, fillRect)
            End Using
        End If

        ' 3. Рамка
        Using borderPen As New Pen(If(ThemeManager.IsDarkTheme, Color.FromArgb(60, 60, 65), Color.FromArgb(200, 200, 200)))
            g.DrawRectangle(borderPen, 0, 0, Me.Width - 1, Me.Height - 1)
        End Using
    End Sub
End Class