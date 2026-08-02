<Global.Microsoft.VisualBasic.CompilerServices.DesignerGenerated()>
Partial Class Form1
    Inherits System.Windows.Forms.Form

    'Form overrides dispose to clean up the component list.
    <System.Diagnostics.DebuggerNonUserCode()>
    Protected Overrides Sub Dispose(ByVal disposing As Boolean)
        Try
            If disposing AndAlso components IsNot Nothing Then
                components.Dispose()
            End If
        Finally
            MyBase.Dispose(disposing)
        End Try
    End Sub

    'Required by the Windows Form Designer
    Private components As System.ComponentModel.IContainer

    'NOTE: The following procedure is required by the Windows Form Designer
    'It can be modified using the Windows Form Designer.  
    'Do not modify it using the code editor.
    <System.Diagnostics.DebuggerStepThrough()>
    Private Sub InitializeComponent()
        components = New ComponentModel.Container()
        Dim resources As System.ComponentModel.ComponentResourceManager = New System.ComponentModel.ComponentResourceManager(GetType(Form1))
        OpenFileDialog1 = New OpenFileDialog()
        TextBox1 = New TextBox()
        Button1 = New Button()
        ComboBox3 = New ComboBox()
        ComboBox1 = New ComboBox()
        Label1 = New Label()
        ComboBox2 = New ComboBox()
        Label3 = New Label()
        Button3 = New Button()
        ProgressBar1 = New ModernProgressBar()
        Label4 = New Label()
        ComboBox4 = New ComboBox()
        Label2 = New Label()
        PictureBox1 = New PictureBox()
        ToolStrip1 = New ToolStrip()
        ToolStripButton1 = New ToolStripButton()
        ToolStripButton2 = New ToolStripButton()
        ToolStripButton3 = New ToolStripButton()
        ToolStripButton4 = New ToolStripButton()
        ToolStripButton5 = New ToolStripButton()
        ToolStripSeparator4 = New ToolStripSeparator()
        ToolStripButton6 = New ToolStripButton()
        ToolStripButton7 = New ToolStripButton()
        ToolStripSeparator1 = New ToolStripSeparator()
        ToolStripButton8 = New ToolStripButton()
        ToolStripButton9 = New ToolStripButton()
        ToolStripSeparator5 = New ToolStripSeparator()
        ToolStripButton10 = New ToolStripButton()
        ToolStripLabel1 = New ToolStripLabel()
        ToolStripSeparator2 = New ToolStripSeparator()
        ToolStripLabel2 = New ToolStripLabel()
        playbackTimer = New Timer(components)
        Label6 = New Label()
        previewTimer = New Timer(components)
        resizeDebounceTimer = New Timer(components)
        monitorTimer = New Timer(components)
        TrackBar1 = New TrackBar()
        ComboBox5 = New ComboBox()
        Label5 = New Label()
        Label7 = New Label()
        MenuStrip1 = New MenuStrip()
        ФайлToolStripMenuItem = New ToolStripMenuItem()
        ОткрытьToolStripMenuItem = New ToolStripMenuItem()
        НастройкиToolStripMenuItem = New ToolStripMenuItem()
        ВыходToolStripMenuItem = New ToolStripMenuItem()
        ПравкаToolStripMenuItem = New ToolStripMenuItem()
        ВырезатьToolStripMenuItem = New ToolStripMenuItem()
        УдалитьToolStripMenuItem = New ToolStripMenuItem()
        ОтменаToolStripMenuItem = New ToolStripMenuItem()
        ДорожкиToolStripMenuItem = New ToolStripMenuItem()
        ДобавитьВидеотрекToolStripMenuItem = New ToolStripMenuItem()
        ДобавитьАудиотрекToolStripMenuItem = New ToolStripMenuItem()
        УдалитьПоследнийТрекToolStripMenuItem = New ToolStripMenuItem()
        ОбновлениеИнтерфейсаToolStripMenuItem = New ToolStripMenuItem()
        СредстваToolStripMenuItem = New ToolStripMenuItem()
        ЗагрузкаToolStripMenuItem = New ToolStripMenuItem()
        ОчиститьБуферToolStripMenuItem = New ToolStripMenuItem()
        ВидеоФайлыToolStripMenuItem = New ToolStripMenuItem()
        ПанельВидеоToolStripMenuItem = New ToolStripMenuItem()
        PictureBox2 = New PictureBox()
        VideoPanel1 = New Panel()
        CType(PictureBox1, ComponentModel.ISupportInitialize).BeginInit()
        ToolStrip1.SuspendLayout()
        CType(TrackBar1, ComponentModel.ISupportInitialize).BeginInit()
        MenuStrip1.SuspendLayout()
        CType(PictureBox2, ComponentModel.ISupportInitialize).BeginInit()
        VideoPanel1.SuspendLayout()
        SuspendLayout()
        ' 
        ' TextBox1
        ' 
        TextBox1.Location = New Point(15, 34)
        TextBox1.Name = "TextBox1"
        TextBox1.Size = New Size(794, 27)
        TextBox1.TabIndex = 0
        TextBox1.Text = "C:\Users\SB5\Source\Repos\yoump\bin\Debug\net7.0-windows\001.mp4"
        ' 
        ' Button1
        ' 
        Button1.Location = New Point(665, 288)
        Button1.Name = "Button1"
        Button1.Size = New Size(144, 35)
        Button1.TabIndex = 3
        Button1.Text = "Конвертировать"
        Button1.UseVisualStyleBackColor = True
        ' 
        ' ComboBox3
        ' 
        ComboBox3.DropDownStyle = ComboBoxStyle.DropDownList
        ComboBox3.FormattingEnabled = True
        ComboBox3.Location = New Point(505, 158)
        ComboBox3.Name = "ComboBox3"
        ComboBox3.Size = New Size(304, 28)
        ComboBox3.TabIndex = 13
        ' 
        ' ComboBox1
        ' 
        ComboBox1.DropDownStyle = ComboBoxStyle.DropDownList
        ComboBox1.FormattingEnabled = True
        ComboBox1.Location = New Point(505, 74)
        ComboBox1.Name = "ComboBox1"
        ComboBox1.Size = New Size(304, 28)
        ComboBox1.TabIndex = 6
        ' 
        ' Label1
        ' 
        Label1.AutoSize = True
        Label1.Font = New Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point, CByte(204))
        Label1.Location = New Point(377, 77)
        Label1.Name = "Label1"
        Label1.Size = New Size(112, 20)
        Label1.TabIndex = 7
        Label1.Text = "Формат видео:"
        ' 
        ' ComboBox2
        ' 
        ComboBox2.DropDownStyle = ComboBoxStyle.DropDownList
        ComboBox2.FormattingEnabled = True
        ComboBox2.Location = New Point(505, 116)
        ComboBox2.Name = "ComboBox2"
        ComboBox2.Size = New Size(304, 28)
        ComboBox2.TabIndex = 9
        ' 
        ' Label3
        ' 
        Label3.AutoSize = True
        Label3.Location = New Point(377, 119)
        Label3.Name = "Label3"
        Label3.Size = New Size(74, 20)
        Label3.TabIndex = 10
        Label3.Text = "GPU, CPU:"
        ' 
        ' Button3
        ' 
        Button3.Location = New Point(698, 337)
        Button3.Name = "Button3"
        Button3.Size = New Size(110, 30)
        Button3.TabIndex = 11
        Button3.Text = "Отмена"
        Button3.UseVisualStyleBackColor = True
        ' 
        ' ProgressBar1
        ' 
        ProgressBar1.Location = New Point(15, 313)
        ProgressBar1.Name = "ProgressBar1"
        ProgressBar1.Size = New Size(625, 29)
        ProgressBar1.TabIndex = 12
        ' 
        ' Label4
        ' 
        Label4.AutoSize = True
        Label4.Location = New Point(377, 161)
        Label4.Name = "Label4"
        Label4.Size = New Size(62, 20)
        Label4.TabIndex = 14
        Label4.Text = "Кодеки:"
        ' 
        ' ComboBox4
        ' 
        ComboBox4.DropDownStyle = ComboBoxStyle.DropDownList
        ComboBox4.FormattingEnabled = True
        ComboBox4.Location = New Point(671, 241)
        ComboBox4.Name = "ComboBox4"
        ComboBox4.Size = New Size(138, 28)
        ComboBox4.TabIndex = 19
        ' 
        ' Label2
        ' 
        Label2.Location = New Point(14, 276)
        Label2.Name = "Label2"
        Label2.Size = New Size(791, 32)
        Label2.TabIndex = 5
        Label2.Text = "Конвертация видео ..."
        ' 
        ' PictureBox1
        ' 
        PictureBox1.BackColor = SystemColors.ActiveCaptionText
        PictureBox1.Location = New Point(15, 382)
        PictureBox1.Name = "PictureBox1"
        PictureBox1.Size = New Size(794, 184)
        PictureBox1.TabIndex = 20
        PictureBox1.TabStop = False
        ' 
        ' ToolStrip1
        ' 
        ToolStrip1.Dock = DockStyle.Bottom
        ToolStrip1.ImageScalingSize = New Size(20, 20)
        ToolStrip1.Items.AddRange(New ToolStripItem() {ToolStripButton1, ToolStripButton2, ToolStripButton3, ToolStripButton4, ToolStripButton5, ToolStripSeparator4, ToolStripButton6, ToolStripButton7, ToolStripSeparator1, ToolStripButton8, ToolStripButton9, ToolStripSeparator5, ToolStripButton10, ToolStripLabel1, ToolStripSeparator2, ToolStripLabel2})
        ToolStrip1.Location = New Point(0, 573)
        ToolStrip1.Name = "ToolStrip1"
        ToolStrip1.RenderMode = ToolStripRenderMode.System
        ToolStrip1.Size = New Size(825, 27)
        ToolStrip1.TabIndex = 21
        ToolStrip1.Text = "ToolStrip1"
        ' 
        ' ToolStripButton1
        ' 
        ToolStripButton1.Image = CType(resources.GetObject("ToolStripButton1.Image"), Image)
        ToolStripButton1.ImageTransparentColor = Color.Magenta
        ToolStripButton1.Name = "ToolStripButton1"
        ToolStripButton1.Size = New Size(29, 24)
        ' 
        ' ToolStripButton2
        ' 
        ToolStripButton2.Image = CType(resources.GetObject("ToolStripButton2.Image"), Image)
        ToolStripButton2.ImageTransparentColor = Color.Magenta
        ToolStripButton2.Name = "ToolStripButton2"
        ToolStripButton2.Size = New Size(29, 24)
        ' 
        ' ToolStripButton3
        ' 
        ToolStripButton3.Image = CType(resources.GetObject("ToolStripButton3.Image"), Image)
        ToolStripButton3.ImageTransparentColor = Color.Magenta
        ToolStripButton3.Name = "ToolStripButton3"
        ToolStripButton3.Size = New Size(29, 24)
        ' 
        ' ToolStripButton4
        ' 
        ToolStripButton4.Image = CType(resources.GetObject("ToolStripButton4.Image"), Image)
        ToolStripButton4.ImageTransparentColor = Color.Magenta
        ToolStripButton4.Name = "ToolStripButton4"
        ToolStripButton4.Size = New Size(29, 24)
        ' 
        ' ToolStripButton5
        ' 
        ToolStripButton5.Image = CType(resources.GetObject("ToolStripButton5.Image"), Image)
        ToolStripButton5.ImageTransparentColor = Color.Magenta
        ToolStripButton5.Name = "ToolStripButton5"
        ToolStripButton5.Size = New Size(29, 24)
        ' 
        ' ToolStripSeparator4
        ' 
        ToolStripSeparator4.Name = "ToolStripSeparator4"
        ToolStripSeparator4.Size = New Size(6, 27)
        ' 
        ' ToolStripButton6
        ' 
        ToolStripButton6.DisplayStyle = ToolStripItemDisplayStyle.Image
        ToolStripButton6.Image = CType(resources.GetObject("ToolStripButton6.Image"), Image)
        ToolStripButton6.ImageTransparentColor = Color.Magenta
        ToolStripButton6.Name = "ToolStripButton6"
        ToolStripButton6.Size = New Size(29, 24)
        ToolStripButton6.Text = "ToolStripButton6"
        ' 
        ' ToolStripButton7
        ' 
        ToolStripButton7.DisplayStyle = ToolStripItemDisplayStyle.Image
        ToolStripButton7.Image = CType(resources.GetObject("ToolStripButton7.Image"), Image)
        ToolStripButton7.ImageTransparentColor = Color.Magenta
        ToolStripButton7.Name = "ToolStripButton7"
        ToolStripButton7.Size = New Size(29, 24)
        ToolStripButton7.Text = "ToolStripButton7"
        ' 
        ' ToolStripSeparator1
        ' 
        ToolStripSeparator1.Name = "ToolStripSeparator1"
        ToolStripSeparator1.Size = New Size(6, 27)
        ' 
        ' ToolStripButton8
        ' 
        ToolStripButton8.DisplayStyle = ToolStripItemDisplayStyle.Image
        ToolStripButton8.Image = CType(resources.GetObject("ToolStripButton8.Image"), Image)
        ToolStripButton8.ImageTransparentColor = Color.Magenta
        ToolStripButton8.Name = "ToolStripButton8"
        ToolStripButton8.Size = New Size(29, 24)
        ToolStripButton8.Text = "ToolStripButton8"
        ' 
        ' ToolStripButton9
        ' 
        ToolStripButton9.DisplayStyle = ToolStripItemDisplayStyle.Image
        ToolStripButton9.Image = CType(resources.GetObject("ToolStripButton9.Image"), Image)
        ToolStripButton9.ImageTransparentColor = Color.Magenta
        ToolStripButton9.Name = "ToolStripButton9"
        ToolStripButton9.Size = New Size(29, 24)
        ToolStripButton9.Text = "ToolStripButton9"
        ' 
        ' ToolStripSeparator5
        ' 
        ToolStripSeparator5.Name = "ToolStripSeparator5"
        ToolStripSeparator5.Size = New Size(6, 27)
        ' 
        ' ToolStripButton10
        ' 
        ToolStripButton10.DisplayStyle = ToolStripItemDisplayStyle.Image
        ToolStripButton10.Image = CType(resources.GetObject("ToolStripButton10.Image"), Image)
        ToolStripButton10.ImageTransparentColor = Color.Magenta
        ToolStripButton10.Name = "ToolStripButton10"
        ToolStripButton10.Size = New Size(29, 24)
        ToolStripButton10.Text = "ToolStripButton10"
        ' 
        ' ToolStripLabel1
        ' 
        ToolStripLabel1.Name = "ToolStripLabel1"
        ToolStripLabel1.Size = New Size(124, 24)
        ToolStripLabel1.Text = "Файл не выбран"
        ' 
        ' ToolStripSeparator2
        ' 
        ToolStripSeparator2.Name = "ToolStripSeparator2"
        ToolStripSeparator2.Size = New Size(6, 27)
        ' 
        ' ToolStripLabel2
        ' 
        ToolStripLabel2.Name = "ToolStripLabel2"
        ToolStripLabel2.Size = New Size(90, 24)
        ToolStripLabel2.Text = "00:00:00.000"
        ' 
        ' playbackTimer
        ' 
        playbackTimer.Interval = 30
        ' 
        ' Label6
        ' 
        Label6.AutoSize = True
        Label6.Location = New Point(15, 351)
        Label6.Name = "Label6"
        Label6.Size = New Size(148, 20)
        Label6.TabIndex = 24
        Label6.Text = "Файл не загружен ..."
        ' 
        ' previewTimer
        ' 
        previewTimer.Interval = 150
        ' 
        ' resizeDebounceTimer
        ' 
        ' 
        ' TrackBar1
        ' 
        TrackBar1.Location = New Point(724, 561)
        TrackBar1.Name = "TrackBar1"
        TrackBar1.Size = New Size(95, 56)
        TrackBar1.TabIndex = 26
        ' 
        ' ComboBox5
        ' 
        ComboBox5.DropDownStyle = ComboBoxStyle.DropDownList
        ComboBox5.FormattingEnabled = True
        ComboBox5.Location = New Point(505, 199)
        ComboBox5.Name = "ComboBox5"
        ComboBox5.Size = New Size(304, 28)
        ComboBox5.TabIndex = 27
        ' 
        ' Label5
        ' 
        Label5.AutoSize = True
        Label5.Location = New Point(377, 202)
        Label5.Name = "Label5"
        Label5.Size = New Size(98, 20)
        Label5.TabIndex = 28
        Label5.Text = "Разрешения:"
        ' 
        ' Label7
        ' 
        Label7.AutoSize = True
        Label7.Location = New Point(540, 244)
        Label7.Name = "Label7"
        Label7.Size = New Size(109, 20)
        Label7.TabIndex = 30
        Label7.Text = "Сжатие видео:"
        ' 
        ' MenuStrip1
        ' 
        MenuStrip1.ImageScalingSize = New Size(20, 20)
        MenuStrip1.Items.AddRange(New ToolStripItem() {ФайлToolStripMenuItem, ПравкаToolStripMenuItem, ДорожкиToolStripMenuItem, СредстваToolStripMenuItem})
        MenuStrip1.Location = New Point(0, 0)
        MenuStrip1.Name = "MenuStrip1"
        MenuStrip1.RenderMode = ToolStripRenderMode.System
        MenuStrip1.Size = New Size(825, 28)
        MenuStrip1.TabIndex = 31
        MenuStrip1.Text = "MenuStrip1"
        ' 
        ' ФайлToolStripMenuItem
        ' 
        ФайлToolStripMenuItem.DropDownItems.AddRange(New ToolStripItem() {ОткрытьToolStripMenuItem, НастройкиToolStripMenuItem, ВыходToolStripMenuItem})
        ФайлToolStripMenuItem.Name = "ФайлToolStripMenuItem"
        ФайлToolStripMenuItem.Size = New Size(59, 24)
        ФайлToolStripMenuItem.Text = "Файл"
        ' 
        ' ОткрытьToolStripMenuItem
        ' 
        ОткрытьToolStripMenuItem.Name = "ОткрытьToolStripMenuItem"
        ОткрытьToolStripMenuItem.Size = New Size(167, 26)
        ОткрытьToolStripMenuItem.Text = "Открыть"
        ' 
        ' НастройкиToolStripMenuItem
        ' 
        НастройкиToolStripMenuItem.Name = "НастройкиToolStripMenuItem"
        НастройкиToolStripMenuItem.Size = New Size(167, 26)
        НастройкиToolStripMenuItem.Text = "Настройки"
        ' 
        ' ВыходToolStripMenuItem
        ' 
        ВыходToolStripMenuItem.Name = "ВыходToolStripMenuItem"
        ВыходToolStripMenuItem.Size = New Size(167, 26)
        ВыходToolStripMenuItem.Text = "Выход"
        ' 
        ' ПравкаToolStripMenuItem
        ' 
        ПравкаToolStripMenuItem.DropDownItems.AddRange(New ToolStripItem() {ВырезатьToolStripMenuItem, УдалитьToolStripMenuItem, ОтменаToolStripMenuItem})
        ПравкаToolStripMenuItem.Name = "ПравкаToolStripMenuItem"
        ПравкаToolStripMenuItem.Size = New Size(74, 24)
        ПравкаToolStripMenuItem.Text = "Правка"
        ' 
        ' ВырезатьToolStripMenuItem
        ' 
        ВырезатьToolStripMenuItem.Name = "ВырезатьToolStripMenuItem"
        ВырезатьToolStripMenuItem.Size = New Size(158, 26)
        ВырезатьToolStripMenuItem.Text = "Вырезать"
        ' 
        ' УдалитьToolStripMenuItem
        ' 
        УдалитьToolStripMenuItem.Name = "УдалитьToolStripMenuItem"
        УдалитьToolStripMenuItem.Size = New Size(158, 26)
        УдалитьToolStripMenuItem.Text = "Удалить"
        ' 
        ' ОтменаToolStripMenuItem
        ' 
        ОтменаToolStripMenuItem.Name = "ОтменаToolStripMenuItem"
        ОтменаToolStripMenuItem.Size = New Size(158, 26)
        ОтменаToolStripMenuItem.Text = "Отмена"
        ' 
        ' ДорожкиToolStripMenuItem
        ' 
        ДорожкиToolStripMenuItem.DropDownItems.AddRange(New ToolStripItem() {ДобавитьВидеотрекToolStripMenuItem, ДобавитьАудиотрекToolStripMenuItem, УдалитьПоследнийТрекToolStripMenuItem, ОбновлениеИнтерфейсаToolStripMenuItem})
        ДорожкиToolStripMenuItem.Name = "ДорожкиToolStripMenuItem"
        ДорожкиToolStripMenuItem.Size = New Size(87, 24)
        ДорожкиToolStripMenuItem.Text = "Дорожки"
        ' 
        ' ДобавитьВидеотрекToolStripMenuItem
        ' 
        ДобавитьВидеотрекToolStripMenuItem.Name = "ДобавитьВидеотрекToolStripMenuItem"
        ДобавитьВидеотрекToolStripMenuItem.Size = New Size(267, 26)
        ДобавитьВидеотрекToolStripMenuItem.Text = "Добавить видео-трек"
        ' 
        ' ДобавитьАудиотрекToolStripMenuItem
        ' 
        ДобавитьАудиотрекToolStripMenuItem.Name = "ДобавитьАудиотрекToolStripMenuItem"
        ДобавитьАудиотрекToolStripMenuItem.Size = New Size(267, 26)
        ДобавитьАудиотрекToolStripMenuItem.Text = "Добавить аудио-трек"
        ' 
        ' УдалитьПоследнийТрекToolStripMenuItem
        ' 
        УдалитьПоследнийТрекToolStripMenuItem.Name = "УдалитьПоследнийТрекToolStripMenuItem"
        УдалитьПоследнийТрекToolStripMenuItem.Size = New Size(267, 26)
        УдалитьПоследнийТрекToolStripMenuItem.Text = "Удалить последний трек"
        ' 
        ' ОбновлениеИнтерфейсаToolStripMenuItem
        ' 
        ОбновлениеИнтерфейсаToolStripMenuItem.Name = "ОбновлениеИнтерфейсаToolStripMenuItem"
        ОбновлениеИнтерфейсаToolStripMenuItem.Size = New Size(267, 26)
        ОбновлениеИнтерфейсаToolStripMenuItem.Text = "Обновление интерфейса"
        ' 
        ' СредстваToolStripMenuItem
        ' 
        СредстваToolStripMenuItem.DropDownItems.AddRange(New ToolStripItem() {ЗагрузкаToolStripMenuItem, ОчиститьБуферToolStripMenuItem, ВидеоФайлыToolStripMenuItem, ПанельВидеоToolStripMenuItem})
        СредстваToolStripMenuItem.Name = "СредстваToolStripMenuItem"
        СредстваToolStripMenuItem.Size = New Size(86, 24)
        СредстваToolStripMenuItem.Text = "Средства"
        ' 
        ' ЗагрузкаToolStripMenuItem
        ' 
        ЗагрузкаToolStripMenuItem.Name = "ЗагрузкаToolStripMenuItem"
        ЗагрузкаToolStripMenuItem.Size = New Size(203, 26)
        ЗагрузкаToolStripMenuItem.Text = "Загрузка"
        ' 
        ' ОчиститьБуферToolStripMenuItem
        ' 
        ОчиститьБуферToolStripMenuItem.Name = "ОчиститьБуферToolStripMenuItem"
        ОчиститьБуферToolStripMenuItem.Size = New Size(203, 26)
        ОчиститьБуферToolStripMenuItem.Text = "Очистить буфер"
        ' 
        ' ВидеоФайлыToolStripMenuItem
        ' 
        ВидеоФайлыToolStripMenuItem.Name = "ВидеоФайлыToolStripMenuItem"
        ВидеоФайлыToolStripMenuItem.Size = New Size(203, 26)
        ВидеоФайлыToolStripMenuItem.Text = "Видео файлы"
        ' 
        ' ПанельВидеоToolStripMenuItem
        ' 
        ПанельВидеоToolStripMenuItem.Name = "ПанельВидеоToolStripMenuItem"
        ПанельВидеоToolStripMenuItem.Size = New Size(203, 26)
        ПанельВидеоToolStripMenuItem.Text = "Панель видео"
        ' 
        ' PictureBox2
        ' 
        PictureBox2.BackColor = SystemColors.ActiveCaptionText
        PictureBox2.Location = New Point(0, -3)
        PictureBox2.Name = "PictureBox2"
        PictureBox2.Size = New Size(345, 195)
        PictureBox2.TabIndex = 32
        PictureBox2.TabStop = False
        ' 
        ' VideoPanel1
        ' 
        VideoPanel1.Controls.Add(PictureBox2)
        VideoPanel1.Location = New Point(15, 74)
        VideoPanel1.Name = "VideoPanel1"
        VideoPanel1.Size = New Size(347, 195)
        VideoPanel1.TabIndex = 33
        ' 
        ' Form1
        ' 
        AutoScaleDimensions = New SizeF(120F, 120F)
        AutoScaleMode = AutoScaleMode.Dpi
        BackColor = SystemColors.Control
        ClientSize = New Size(825, 600)
        Controls.Add(VideoPanel1)
        Controls.Add(Label7)
        Controls.Add(Label5)
        Controls.Add(ComboBox5)
        Controls.Add(TrackBar1)
        Controls.Add(Label6)
        Controls.Add(ToolStrip1)
        Controls.Add(MenuStrip1)
        Controls.Add(PictureBox1)
        Controls.Add(Button3)
        Controls.Add(ProgressBar1)
        Controls.Add(ComboBox4)
        Controls.Add(Label4)
        Controls.Add(ComboBox3)
        Controls.Add(Label3)
        Controls.Add(ComboBox2)
        Controls.Add(Label1)
        Controls.Add(ComboBox1)
        Controls.Add(Button1)
        Controls.Add(TextBox1)
        Controls.Add(Label2)
        FormBorderStyle = FormBorderStyle.FixedSingle
        Icon = CType(resources.GetObject("$this.Icon"), Icon)
        MainMenuStrip = MenuStrip1
        MaximizeBox = False
        MinimizeBox = False
        Name = "Form1"
        Text = "YouMP Media Pro"
        CType(PictureBox1, ComponentModel.ISupportInitialize).EndInit()
        ToolStrip1.ResumeLayout(False)
        ToolStrip1.PerformLayout()
        CType(TrackBar1, ComponentModel.ISupportInitialize).EndInit()
        MenuStrip1.ResumeLayout(False)
        MenuStrip1.PerformLayout()
        CType(PictureBox2, ComponentModel.ISupportInitialize).EndInit()
        VideoPanel1.ResumeLayout(False)
        ResumeLayout(False)
        PerformLayout()
    End Sub
    Friend WithEvents OpenFileDialog1 As OpenFileDialog
    Friend WithEvents TextBox1 As TextBox
    Friend WithEvents Button1 As Button
    Friend WithEvents ComboBox3 As ComboBox
    Friend WithEvents ComboBox1 As ComboBox
    Friend WithEvents Label1 As Label
    Friend WithEvents ComboBox2 As ComboBox
    Friend WithEvents Label3 As Label
    Friend WithEvents Button3 As Button
    Friend WithEvents ProgressBar1 As ModernProgressBar
    Friend WithEvents Label4 As Label
    Friend WithEvents ComboBox4 As ComboBox
    Friend WithEvents Label2 As Label
    Friend WithEvents PictureBox1 As PictureBox
    Friend WithEvents ToolStrip1 As ToolStrip
    Friend WithEvents ToolStripButton1 As ToolStripButton
    Friend WithEvents ToolStripButton2 As ToolStripButton
    Friend WithEvents playbackTimer As Timer
    Friend WithEvents ToolStripButton3 As ToolStripButton
    Friend WithEvents ToolStripButton4 As ToolStripButton
    Friend WithEvents ToolStripButton5 As ToolStripButton
    Friend WithEvents ToolStripSeparator1 As ToolStripSeparator
    Friend WithEvents ToolStripLabel1 As ToolStripLabel
    Friend WithEvents Label6 As Label
    Friend WithEvents previewTimer As Timer
    Friend WithEvents resizeDebounceTimer As Timer
    Friend WithEvents monitorTimer As Timer
    Friend WithEvents ToolStripSeparator2 As ToolStripSeparator
    Friend WithEvents ToolStripLabel2 As ToolStripLabel
    Friend WithEvents TrackBar1 As TrackBar
    Friend WithEvents ToolStripSeparator4 As ToolStripSeparator
    Friend WithEvents ToolStripButton6 As ToolStripButton
    Friend WithEvents ToolStripButton7 As ToolStripButton
    Friend WithEvents ComboBox5 As ComboBox
    Friend WithEvents Label5 As Label
    Friend WithEvents Label7 As Label
    Friend WithEvents ToolStripButton8 As ToolStripButton
    Friend WithEvents ToolStripButton9 As ToolStripButton
    Friend WithEvents ToolStripSeparator5 As ToolStripSeparator
    Friend WithEvents ToolStripButton10 As ToolStripButton
    Friend WithEvents MenuStrip1 As MenuStrip
    Friend WithEvents ФайлToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents НастройкиToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents ВыходToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents PictureBox2 As PictureBox
    Friend WithEvents ОткрытьToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents VideoPanel1 As Panel
    Friend WithEvents СредстваToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents ЗагрузкаToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents ОчиститьБуферToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents ВидеоФайлыToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents ПравкаToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents ВырезатьToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents УдалитьToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents ОтменаToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents ДорожкиToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents ДобавитьВидеотрекToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents ДобавитьАудиотрекToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents УдалитьПоследнийТрекToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents ОбновлениеИнтерфейсаToolStripMenuItem As ToolStripMenuItem
    Friend WithEvents ПанельВидеоToolStripMenuItem As ToolStripMenuItem
End Class

