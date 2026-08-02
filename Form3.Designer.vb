<Global.Microsoft.VisualBasic.CompilerServices.DesignerGenerated()> _
Partial Class Form3
    Inherits System.Windows.Forms.Form

    'Форма переопределяет dispose для очистки списка компонентов.
    <System.Diagnostics.DebuggerNonUserCode()> _
    Protected Overrides Sub Dispose(ByVal disposing As Boolean)
        Try
            If disposing AndAlso components IsNot Nothing Then
                components.Dispose()
            End If
        Finally
            MyBase.Dispose(disposing)
        End Try
    End Sub

    'Является обязательной для конструктора форм Windows Forms
    Private components As System.ComponentModel.IContainer

    'Примечание: следующая процедура является обязательной для конструктора форм Windows Forms
    'Для ее изменения используйте конструктор форм Windows Form.  
    'Не изменяйте ее в редакторе исходного кода.
    <System.Diagnostics.DebuggerStepThrough()> _
    Private Sub InitializeComponent()
        TextBox1 = New TextBox()
        Button1 = New Button()
        OpenFileDialog1 = New OpenFileDialog()
        Button2 = New Button()
        Button3 = New Button()
        Label1 = New Label()
        ComboBox1 = New ComboBox()
        Label2 = New Label()
        Label3 = New Label()
        Button4 = New Button()
        ComboBox2 = New ComboBox()
        Label4 = New Label()
        ComboBox3 = New ComboBox()
        SuspendLayout()
        ' 
        ' TextBox1
        ' 
        TextBox1.Location = New Point(15, 52)
        TextBox1.Name = "TextBox1"
        TextBox1.Size = New Size(577, 27)
        TextBox1.TabIndex = 0
        ' 
        ' Button1
        ' 
        Button1.Location = New Point(498, 94)
        Button1.Name = "Button1"
        Button1.Size = New Size(94, 29)
        Button1.TabIndex = 1
        Button1.Text = "Обзор"
        Button1.UseVisualStyleBackColor = True
        ' 
        ' Button2
        ' 
        Button2.Location = New Point(485, 328)
        Button2.Name = "Button2"
        Button2.Size = New Size(107, 31)
        Button2.TabIndex = 2
        Button2.Text = "Применить"
        Button2.UseVisualStyleBackColor = True
        ' 
        ' Button3
        ' 
        Button3.Location = New Point(372, 328)
        Button3.Name = "Button3"
        Button3.Size = New Size(107, 31)
        Button3.TabIndex = 3
        Button3.Text = "ОК"
        Button3.UseVisualStyleBackColor = True
        ' 
        ' Label1
        ' 
        Label1.AutoSize = True
        Label1.Location = New Point(15, 24)
        Label1.Name = "Label1"
        Label1.Size = New Size(229, 20)
        Label1.TabIndex = 4
        Label1.Text = "Директория сохранения видео:"
        ' 
        ' ComboBox1
        ' 
        ComboBox1.DropDownStyle = ComboBoxStyle.DropDownList
        ComboBox1.FormattingEnabled = True
        ComboBox1.Location = New Point(15, 137)
        ComboBox1.Name = "ComboBox1"
        ComboBox1.Size = New Size(151, 28)
        ComboBox1.TabIndex = 5
        ' 
        ' Label2
        ' 
        Label2.AutoSize = True
        Label2.Location = New Point(15, 103)
        Label2.Name = "Label2"
        Label2.Size = New Size(284, 20)
        Label2.TabIndex = 6
        Label2.Text = "Смена темы требоваться перезагрузка:"
        ' 
        ' Label3
        ' 
        Label3.AutoSize = True
        Label3.Location = New Point(12, 184)
        Label3.Name = "Label3"
        Label3.Size = New Size(175, 20)
        Label3.TabIndex = 7
        Label3.Text = "Просмотор лог файлов:"
        ' 
        ' Button4
        ' 
        Button4.Location = New Point(15, 219)
        Button4.Name = "Button4"
        Button4.Size = New Size(104, 29)
        Button4.TabIndex = 8
        Button4.Text = "Открыть"
        Button4.UseVisualStyleBackColor = True
        ' 
        ' ComboBox2
        ' 
        ComboBox2.DropDownStyle = ComboBoxStyle.DropDownList
        ComboBox2.FormattingEnabled = True
        ComboBox2.Location = New Point(345, 176)
        ComboBox2.Name = "ComboBox2"
        ComboBox2.Size = New Size(247, 28)
        ComboBox2.TabIndex = 9
        ' 
        ' Label4
        ' 
        Label4.AutoSize = True
        Label4.Location = New Point(345, 144)
        Label4.Name = "Label4"
        Label4.Size = New Size(217, 20)
        Label4.TabIndex = 10
        Label4.Text = "Переключение аудио движка:"
        ' 
        ' ComboBox3
        ' 
        ComboBox3.DropDownStyle = ComboBoxStyle.DropDownList
        ComboBox3.FormattingEnabled = True
        ComboBox3.Location = New Point(15, 271)
        ComboBox3.Name = "ComboBox3"
        ComboBox3.Size = New Size(360, 28)
        ComboBox3.TabIndex = 11
        ' 
        ' Form3
        ' 
        AutoScaleDimensions = New SizeF(120F, 120F)
        AutoScaleMode = AutoScaleMode.Dpi
        ClientSize = New Size(608, 371)
        Controls.Add(ComboBox3)
        Controls.Add(Label4)
        Controls.Add(ComboBox2)
        Controls.Add(Button4)
        Controls.Add(Label3)
        Controls.Add(Label2)
        Controls.Add(ComboBox1)
        Controls.Add(Label1)
        Controls.Add(Button3)
        Controls.Add(Button2)
        Controls.Add(Button1)
        Controls.Add(TextBox1)
        FormBorderStyle = FormBorderStyle.FixedSingle
        MaximizeBox = False
        MinimizeBox = False
        Name = "Form3"
        Text = "Настройки"
        ResumeLayout(False)
        PerformLayout()
    End Sub

    Friend WithEvents TextBox1 As TextBox
    Friend WithEvents Button1 As Button
    Friend WithEvents OpenFileDialog1 As OpenFileDialog
    Friend WithEvents Button2 As Button
    Friend WithEvents Button3 As Button
    Friend WithEvents Label1 As Label
    Friend WithEvents ComboBox1 As ComboBox
    Friend WithEvents Label2 As Label
    Friend WithEvents Label3 As Label
    Friend WithEvents Button4 As Button
    Friend WithEvents ComboBox2 As ComboBox
    Friend WithEvents Label4 As Label
    Friend WithEvents ComboBox3 As ComboBox
End Class
