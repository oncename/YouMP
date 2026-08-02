' START OF FILE Form3.vb
Option Strict On
Option Explicit On

Imports System.Drawing
Imports System.Windows.Forms
Imports System.IO
Imports System.Text
Imports System.Linq

Public Class Form3
    Inherits Form

    Private ReadOnly filePath As String = Path.Combine(Application.StartupPath, "system.txt")
    Private _isDarkTheme As Boolean = True

    Private Sub Form3_Load(sender As Object, e As EventArgs) Handles MyBase.Load
        _isDarkTheme = ThemeManager.IsDarkTheme

        ' Гарантируем наличие элементов для темы
        If ComboBox1 IsNot Nothing AndAlso ComboBox1.Items.Count = 0 Then
            ComboBox1.Items.Add("Темная")
            ComboBox1.Items.Add("Светлая")
        End If

        ' Гарантируем наличие элементов для аудиодвижка
        If ComboBox2 IsNot Nothing AndAlso ComboBox2.Items.Count = 0 Then
            ComboBox2.Items.Add("WASAPI (Windows Audio)")
            ComboBox2.Items.Add("ASIO (Low Latency)")
        End If

        ' Гарантируем наличие элементов для настройки производительности таймлайна
        If ComboBox3 IsNot Nothing AndAlso ComboBox3.Items.Count = 0 Then
            ComboBox3.Items.Add("Сплошная лента (Отображать все кадры)")
            ComboBox3.Items.Add("Начало и Конец (Оптимизированный)")
            ComboBox3.Items.Add("Только начало (Минимальный для слабых ПК)")
        End If

        ' Читаем настройки через единый сервис
        Dim settings As AppSettings = SettingsService.Instance.Current

        TextBox1.Text = settings.DownloadsDirectory

        If settings.IsDarkTheme Then
            ComboBox1.SelectedItem = "Темная"
        Else
            ComboBox1.SelectedItem = "Светлая"
        End If

        If settings.AudioEngine = "ASIO" AndAlso ComboBox2 IsNot Nothing Then
            ComboBox2.SelectedItem = "ASIO (Low Latency)"
        ElseIf ComboBox2 IsNot Nothing Then
            ComboBox2.SelectedItem = "WASAPI (Windows Audio)"
        End If

        ' Загрузка текущего стиля таймлайна из настроек
        If ComboBox3 IsNot Nothing Then
            Dim mode As Integer = settings.TimelineThumbMode
            If mode >= 0 AndAlso mode < ComboBox3.Items.Count Then
                ComboBox3.SelectedIndex = mode
            Else
                ComboBox3.SelectedIndex = 0
            End If
        End If

        ApplyForm3Theme()
    End Sub

    ' Применение режима эскизов таймлайна на лету
    Private Sub ComboBox3_SelectedIndexChanged(sender As Object, e As EventArgs) Handles ComboBox3.SelectedIndexChanged
        If ComboBox3.SelectedIndex < 0 Then Return

        Dim selectedModeIndex As Integer = ComboBox3.SelectedIndex

        ' Применяем новый режим мгновенно "на лету" к уже открытому таймлайну
        Dim mainForm As Form1 = TryCast(Application.OpenForms("Form1"), Form1)
        If mainForm IsNot Nothing Then
            Dim fieldInfo = GetType(Form1).GetField("_tileRendererRef", Reflection.BindingFlags.NonPublic Or Reflection.BindingFlags.Instance)
            If fieldInfo IsNot Nothing Then
                Dim renderer As TileTimelineRenderer = TryCast(fieldInfo.GetValue(mainForm), TileTimelineRenderer)
                If renderer IsNot Nothing Then
                    ' Свойство принимает Integer напрямую. 
                    ' При изменении значения срабатывает SafeInvalidate() и таймлайн перерисуется мгновенно.
                    renderer.TimelineThumbMode = selectedModeIndex
                End If
            End If
        End If
    End Sub

    Private Sub Button2_Click(sender As Object, e As EventArgs) Handles Button2.Click
        Try
            Dim settings As AppSettings = SettingsService.Instance.Current

            ' Обновляем модель данных на основе UI
            settings.DownloadsDirectory = TextBox1.Text.Trim()
            settings.IsDarkTheme = (ComboBox1.Text = "Темная")

            If ComboBox2 IsNot Nothing AndAlso ComboBox2.Text.Contains("ASIO") Then
                settings.AudioEngine = "ASIO"
            Else
                settings.AudioEngine = "WASAPI"
            End If

            ' Сохраняем режим производительности таймлайна
            If ComboBox3 IsNot Nothing AndAlso ComboBox3.SelectedIndex >= 0 Then
                settings.TimelineThumbMode = ComboBox3.SelectedIndex
            End If

            ' Сохраняем в JSON файл
            SettingsService.Instance.Save()

            Dim themeChanged As Boolean = (_isDarkTheme <> settings.IsDarkTheme)
            _isDarkTheme = settings.IsDarkTheme

            ' Инициируем глобальную смену темы
            ThemeManager.ApplyGlobalTheme(_isDarkTheme)
            ApplyForm3Theme()
            Me.Refresh()

            MessageBox.Show("Настройки успешно сохранены! Для применения смены аудиодвижка необходим перезапуск программы.", "Успех", MessageBoxButtons.OK, MessageBoxIcon.Information)
        Catch ex As Exception
            MessageBox.Show($"Не удалось сохранить настройки: {ex.Message}", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error)
        End Try
    End Sub

    Public Sub ApplyForm3Theme()
        ThemeManager.ApplyDwm(Me.Handle)
        Me.BackColor = ThemeManager.BackColor
        Me.ForeColor = ThemeManager.ForeColor
        StyleControls(Me.Controls)
        Me.Refresh()
    End Sub

    Private Sub StyleControls(controls As Control.ControlCollection)
        For Each ctrl As Control In controls
            If ctrl.HasChildren Then
                StyleControls(ctrl.Controls)
            End If

            If TypeOf ctrl Is Button Then
                Dim btn As Button = DirectCast(ctrl, Button)
                btn.FlatStyle = FlatStyle.Flat
                btn.FlatAppearance.BorderSize = 1
                btn.FlatAppearance.BorderColor = If(_isDarkTheme, Color.FromArgb(60, 60, 65), Color.FromArgb(150, 150, 150))
                btn.BackColor = ThemeManager.ControlBackColor
                btn.ForeColor = ThemeManager.ForeColor
            ElseIf TypeOf ctrl Is Label Then
                Dim lbl As Label = DirectCast(ctrl, Label)
                lbl.BackColor = Color.Transparent
                lbl.ForeColor = ThemeManager.ForeColor
            ElseIf TypeOf ctrl Is ComboBox Then
                Dim cb As ComboBox = DirectCast(ctrl, ComboBox)
                cb.FlatStyle = FlatStyle.Flat
                cb.BackColor = ThemeManager.ControlBackColor
                cb.ForeColor = ThemeManager.ForeColor
            ElseIf TypeOf ctrl Is TextBox Then
                Dim tb As TextBox = DirectCast(ctrl, TextBox)
                tb.BorderStyle = BorderStyle.FixedSingle
                tb.BackColor = ThemeManager.ControlBackColor
                tb.ForeColor = ThemeManager.ForeColor
            End If
        Next
    End Sub

    Private Sub Button3_Click(sender As Object, e As EventArgs) Handles Button3.Click
        Me.Close()
    End Sub

    Private Sub Button4_Click(sender As Object, e As EventArgs) Handles Button4.Click
        Try
            Dim logDir As String = IO.Path.Combine(Application.StartupPath, "logs")
            If IO.Directory.Exists(logDir) Then
                Dim logFiles As IO.FileInfo() = New IO.DirectoryInfo(logDir).GetFiles("log*.txt")
                If logFiles.Length > 0 Then
                    Dim latestLog As IO.FileInfo = logFiles.OrderByDescending(Function(f) f.LastWriteTime).First()
                    Dim startInfo As New System.Diagnostics.ProcessStartInfo() With {.FileName = latestLog.FullName, .UseShellExecute = True}
                    System.Diagnostics.Process.Start(startInfo)
                    Return
                End If
            End If
            MessageBox.Show("Файлы логов не найдены.", "Файлы не найдены", MessageBoxButtons.OK, MessageBoxIcon.Information)
        Catch ex As Exception
            MessageBox.Show($"Не удалось открыть файл логов: {ex.Message}", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error)
        End Try
    End Sub
End Class