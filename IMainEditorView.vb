Option Strict On
Option Explicit On

Imports System
Imports System.Drawing
Imports yoump.IServices

''' <summary>
''' Интерфейс главного окна редактора (Passive View).
''' Предоставляет Presenter'у рычаги для управления пользовательским интерфейсом.
''' </summary>
Public Interface IMainEditorView

    ' =================================================================
    ' СУЩЕСТВУЮЩИЕ СВОЙСТВА СОСТОЯНИЯ
    ' =================================================================
    Property MarkerStart As TimeSpan
    Property MarkerEnd As TimeSpan

    ' =================================================================
    ' СУЩЕСТВУЮЩИЕ СОБЫТИЯ УПРАВЛЕНИЯ ТАЙМЛАЙНОМ
    ' =================================================================
    Event CutRequested As EventHandler
    Event ClearCutsRequested As EventHandler
    Event ZoomInRequested As EventHandler
    Event ZoomOutRequested As EventHandler
    Event PlaybackTick As EventHandler(Of Long)

    ' =================================================================
    ' МЕТОДЫ ИНФОРМИРОВАНИЯ ПОЛЬЗОВАТЕЛЯ
    ' =================================================================
    Sub ShowInfoMessage(text As String)
    Sub ShowWarningMessage(text As String)

    ' =================================================================
    ' УПРАВЛЕНИЕ ПЛЕЕРОМ И UI
    ' =================================================================
    Sub RequestPlayerSeek(physicalTime As TimeSpan)
    Sub UpdatePlayheadUI(virtualTime As TimeSpan)
    Sub StopPlayerUI()
    Sub RenderTimelineState(stateData As TimelineStateData, fps As Double, hasSelection As Boolean, isAudioReplaced As Boolean, hasAudio As Boolean)

    ' =================================================================
    ' КОНТРАКТЫ ДЛЯ МОДУЛЯ ЭКСПОРТА (Шаг 2 Рефакторинга)
    ' =================================================================
    ''' <summary>
    ''' Запрашивает у пользователя подтверждение на перезапись существующего файла.
    ''' </summary>
    Function AskOverwrite(filePath As String) As Boolean

    ''' <summary>
    ''' Обновляет визуальные индикаторы прогресса (ProgressBar и Label).
    ''' </summary>
    Sub UpdateExportProgress(percentage As Integer, timeRemaining As String)

    ''' <summary>
    ''' Блокирует или разблокирует элементы управления на время конвертации.
    ''' </summary>
    Sub SetExportState(isExporting As Boolean)

    ' =================================================================
    ' КОНТРАКТЫ ЗАГРУЗКИ И ПРЕДПРОСМОТРА (Шаг 1 Рефакторинга)
    ' =================================================================
    Sub ShowLoadingState(message As String)
    Sub HideLoadingState()
    Sub SetPreviewImage(bmp As Bitmap)
    Sub UpdateMediaInfoUI(infoText As String, hasMedia As Boolean)
    Sub UpdateResolutionProfiles(width As Integer, height As Integer)
    Sub SetHardwareControlsState(isAudioOnly As Boolean)

End Interface