Option Strict On
Option Explicit On

Imports System.IO
Imports System.Text.Json
Imports System.Windows.Forms

Public Class AppSettings
    Public Property DownloadsDirectory As String = ""
    Public Property IsDarkTheme As Boolean = True
    Public Property AudioEngine As String = "WASAPI"
    Public Property AudioBufferMs As Integer = 100

    ' НОВОЕ: Настройка режима отображения таймлайна 
    ' (0 = Все кадры, 1 = Начало и Конец, 2 = Только Начало)
    Public Property TimelineThumbMode As Integer = 0
End Class

Public NotInheritable Class SettingsService
    ' Потокобезопасная реализация Singleton
    Private Shared ReadOnly _instance As New Lazy(Of SettingsService)(Function() New SettingsService())

    ' Кэшированный и потокобезопасный экземпляр настроек сериализатора (Правило CA1869)
    Private Shared ReadOnly _jsonOptions As New JsonSerializerOptions With {
        .WriteIndented = True,
        .PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    }

    Public Shared ReadOnly Property Instance As SettingsService
        Get
            Return _instance.Value
        End Get
    End Property

    Private ReadOnly _settingsFilePath As String
    Private ReadOnly _syncLock As New Object()

    Public Property Current As AppSettings

    Private Sub New()
        _settingsFilePath = Path.Combine(Application.StartupPath, "settings.json")
        Current = New AppSettings()
        Load()
    End Sub

    Public Sub Load()
        SyncLock _syncLock
            If File.Exists(_settingsFilePath) Then
                Try
                    Dim json As String = File.ReadAllText(_settingsFilePath)
                    ' Используем закэшированный экземпляр _jsonOptions
                    Dim loadedSettings As AppSettings = JsonSerializer.Deserialize(Of AppSettings)(json, _jsonOptions)
                    If loadedSettings IsNot Nothing Then
                        Current = loadedSettings
                    End If
                Catch ex As Exception
                    System.Diagnostics.Debug.WriteLine($"[SettingsService] Ошибка чтения настроек: {ex.Message}")
                    Current = New AppSettings()
                End Try
            End If

            ' Устанавливаем пути по умолчанию, если они пусты
            If String.IsNullOrWhiteSpace(Current.DownloadsDirectory) Then
                Current.DownloadsDirectory = Path.Combine(Application.StartupPath, "downloads")
                Save() ' Сохраняем файл по умолчанию
            End If
        End SyncLock
    End Sub

    Public Sub Save()
        SyncLock _syncLock
            Try
                ' Используем закэшированный экземпляр _jsonOptions без создания нового объекта
                Dim json As String = JsonSerializer.Serialize(Current, _jsonOptions)
                File.WriteAllText(_settingsFilePath, json)
            Catch ex As Exception
                System.Diagnostics.Debug.WriteLine($"[SettingsService] Ошибка сохранения настроек: {ex.Message}")
            End Try
        End SyncLock
    End Sub
End Class