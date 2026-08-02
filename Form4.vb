Option Strict On
Option Explicit On

Imports System.Drawing
Imports System.Windows.Forms
Imports System.Linq

Public Class Form4
    Inherits Form

    ' Ссылки на основные компоненты из Form1
    Public Model As ProjectModel
    Public DirectPlayer As Direct3D11VideoPlayer
    Public TileRendererRef As TileTimelineRenderer
    Public ActionPushState As Action
    Public ActionForceRealtimeUpdate As Action ' НОВОЕ: Делегат для обновления на паузе

    Private trkScale As TrackBar
    Private trkPosX As TrackBar
    Private trkPosY As TrackBar
    Private trkRot As TrackBar
    Private lblScaleVal, lblPosXVal, lblPosYVal, lblRotVal As Label

    Private _isUpdatingInspector As Boolean = False
    Private _clipStateBeforeTransform As MediaClip = Nothing

    Public Sub New()
        InitializeComponent() ' Обязательно добавить!

        ' Настройки окна инструмента
        Me.Text = "Инспектор свойств"
        Me.FormBorderStyle = FormBorderStyle.FixedToolWindow
        Me.StartPosition = FormStartPosition.Manual
        Me.ShowInTaskbar = False
        Me.TopMost = True
        Me.Width = 240
        Me.Height = 360
    End Sub

    Private Sub Form4_Load(sender As Object, e As EventArgs) Handles MyBase.Load
        ' Применяем стили темы
        ThemeManager.ApplyDwm(Me.Handle)
        Me.BackColor = ThemeManager.PanelBackColor
        Me.ForeColor = ThemeManager.ForeColor

        Me.Padding = New Padding(10)

        ' Вспомогательная функция для создания блоков
        Dim addPropertyBlock = Sub(title As String, min As Integer, max As Integer, def As Integer,
                                   ByRef outTrk As TrackBar, ByRef outLblVal As Label)

                                   Dim pnlBlock As New Panel() With {.Dock = DockStyle.Top, .Height = 65}

                                   Dim lblName As New Label() With {
                                       .Text = title, .ForeColor = Color.DarkGray, .AutoSize = True,
                                       .Location = New Point(5, 5), .Font = New Font("Segoe UI", 9.0F)
                                   }
                                   outLblVal = New Label() With {
                                       .Text = def.ToString(), .ForeColor = ThemeManager.ForeColor, .AutoSize = False,
                                       .TextAlign = ContentAlignment.MiddleRight, .Width = 55,
                                       .Location = New Point(Me.ClientSize.Width - 75, 5), .Font = New Font("Segoe UI", 9.0F, FontStyle.Bold)
                                   }
                                   outTrk = New TrackBar() With {
                                       .Minimum = min, .Maximum = max, .Value = def,
                                       .TickStyle = TickStyle.None, .AutoSize = False,
                                       .Height = 30, .Width = Me.ClientSize.Width - 20,
                                       .Location = New Point(5, 25), .BackColor = Me.BackColor
                                   }

                                   AddHandler outTrk.Scroll, AddressOf InspectorTrackBar_Scroll
                                   AddHandler outTrk.MouseDown, AddressOf InspectorTrackBar_MouseDown
                                   AddHandler outTrk.MouseUp, AddressOf InspectorTrackBar_MouseUp

                                   pnlBlock.Controls.Add(lblName)
                                   pnlBlock.Controls.Add(outLblVal)
                                   pnlBlock.Controls.Add(outTrk)
                                   Me.Controls.Add(pnlBlock)
                                   pnlBlock.BringToFront()
                               End Sub

        ' Порядок создания блоков (снизу вверх из-за DockStyle.Top)
        addPropertyBlock("Вращение (град):", -180, 180, 0, trkRot, lblRotVal)
        addPropertyBlock("Смещение Y (px):", -2160, 2160, 0, trkPosY, lblPosYVal)
        addPropertyBlock("Смещение X (px):", -3840, 3840, 0, trkPosX, lblPosXVal)
        addPropertyBlock("Масштаб (%):", 10, 500, 100, trkScale, lblScaleVal)

        ' Кнопка сброса
        Dim btnReset As New Button() With {
            .Text = "СБРОСИТЬ", .Dock = DockStyle.Top, .Height = 35, .FlatStyle = FlatStyle.Flat,
            .ForeColor = Color.White, .BackColor = Color.FromArgb(60, 60, 65),
            .Cursor = Cursors.Hand
        }
        btnReset.FlatAppearance.BorderSize = 0
        AddHandler btnReset.Click, AddressOf InspectorBtnReset_Click
        Me.Controls.Add(btnReset)
        btnReset.BringToFront()
    End Sub

    ' Метод для загрузки данных выделенного клипа в UI
    Public Sub LoadClipData(clip As MediaClip)
        _isUpdatingInspector = True

        trkScale.Value = Math.Max(trkScale.Minimum, Math.Min(trkScale.Maximum, CInt(clip.Scale * 100)))
        trkPosX.Value = Math.Max(trkPosX.Minimum, Math.Min(trkPosX.Maximum, CInt(clip.PositionX)))
        trkPosY.Value = Math.Max(trkPosY.Minimum, Math.Min(trkPosY.Maximum, CInt(clip.PositionY)))
        trkRot.Value = Math.Max(trkRot.Minimum, Math.Min(trkRot.Maximum, CInt(clip.Rotation)))

        lblScaleVal.Text = $"{trkScale.Value}%"
        lblPosXVal.Text = trkPosX.Value.ToString()
        lblPosYVal.Text = trkPosY.Value.ToString()
        lblRotVal.Text = $"{trkRot.Value}°"

        _isUpdatingInspector = False
    End Sub

    Private Sub InspectorTrackBar_MouseDown(sender As Object, e As MouseEventArgs)
        If TileRendererRef?.SelectedClip IsNot Nothing Then
            _clipStateBeforeTransform = TileRendererRef.SelectedClip.Clone()
        End If
    End Sub

    Private Sub InspectorTrackBar_Scroll(sender As Object, e As EventArgs)
        If _isUpdatingInspector OrElse TileRendererRef?.SelectedClip Is Nothing Then Return

        Dim clip = TileRendererRef.SelectedClip

        ' Обновляем значения в модели клипа
        clip.Scale = trkScale.Value / 100.0F
        clip.PositionX = trkPosX.Value
        clip.PositionY = trkPosY.Value
        clip.Rotation = trkRot.Value

        ' Обновляем текстовые метки
        lblScaleVal.Text = $"{trkScale.Value}%"
        lblPosXVal.Text = trkPosX.Value.ToString()
        lblPosYVal.Text = trkPosY.Value.ToString()
        lblRotVal.Text = $"{trkRot.Value}°"

        ' Форсируем аппаратную перерисовку текущего кадра (даже на паузе)
        ActionForceRealtimeUpdate?.Invoke()
    End Sub

    Private Sub InspectorTrackBar_MouseUp(sender As Object, e As MouseEventArgs)
        If _clipStateBeforeTransform IsNot Nothing AndAlso TileRendererRef?.SelectedClip IsNot Nothing Then
            Dim clip = TileRendererRef.SelectedClip
            Dim trackIdx = GetTrackIndexForClip(clip.Id)

            If trackIdx >= 0 Then
                Model.UpdateClipStateWithHistory(clip.Id, clip.Clone(), trackIdx, _clipStateBeforeTransform, trackIdx)
            End If

            _clipStateBeforeTransform = Nothing
            ActionPushState?.Invoke() ' Обновляем UI таймлайна (появление звездочки)
        End If
    End Sub

    Private Sub InspectorBtnReset_Click(sender As Object, e As EventArgs)
        If TileRendererRef?.SelectedClip Is Nothing Then Return

        Dim clip = TileRendererRef.SelectedClip
        _clipStateBeforeTransform = clip.Clone()

        trkScale.Value = 100
        trkPosX.Value = 0
        trkPosY.Value = 0
        trkRot.Value = 0

        InspectorTrackBar_Scroll(Nothing, EventArgs.Empty)
        InspectorTrackBar_MouseUp(Nothing, Nothing)
    End Sub

    Private Function GetTrackIndexForClip(clipId As Guid) As Integer
        If Model Is Nothing Then Return -1
        For i As Integer = 0 To Model.Tracks.Count - 1
            If Model.Tracks(i).Clips.Any(Function(c) c.Id = clipId) Then Return i
        Next
        Return -1
    End Function

    ' Перехватываем закрытие, чтобы просто скрывать окно (не уничтожая объект)
    Private Sub Form4_FormClosing(sender As Object, e As FormClosingEventArgs) Handles MyBase.FormClosing
        If e.CloseReason = CloseReason.UserClosing Then
            e.Cancel = True
            Me.Hide()
        End If
    End Sub
End Class