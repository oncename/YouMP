Option Strict On
Option Explicit On

Imports SharpDX.Mathematics.Interop

Public Module ThemePalette
    ' Вспомогательная функция для перевода стандартного RGB(A) (0-255) в формат Direct2D (0.0 - 1.0)
    Private Function FromRgb(r As Integer, g As Integer, b As Integer, Optional a As Integer = 255) As RawColor4
        Return New RawColor4(r / 255.0F, g / 255.0F, b / 255.0F, a / 255.0F)
    End Function

    ' Структура всех возможных цветов таймлайна
    Public Structure PaletteColors
        Public Background As RawColor4
        Public Text As RawColor4
        Public Outline As RawColor4
        Public Placeholder As RawColor4
        Public TickLong As RawColor4
        Public TickShort As RawColor4
        Public Cursor As RawColor4
        Public TooltipBg As RawColor4
        Public TooltipBorder As RawColor4
        Public LoadingBg As RawColor4
        Public AudioBg As RawColor4
        Public AudioCenterLine As RawColor4
        Public AudioSeparator As RawColor4
        Public AudioBorder As RawColor4
        Public AudioLabelBg As RawColor4
        Public AudioLabelBorder As RawColor4
        Public AudioLabelText As RawColor4
        Public PlayheadBorder As RawColor4
        Public MarkerBorder As RawColor4
    End Structure

    ' Константы темной темы
    Public ReadOnly Dark As New PaletteColors With {
        .Background = FromRgb(20, 20, 24),
        .Text = FromRgb(255, 255, 255),
        .Outline = FromRgb(0, 0, 0, 150),
        .Placeholder = FromRgb(80, 80, 90, 127),
        .TickLong = FromRgb(255, 216, 216, 216),
        .TickShort = FromRgb(255, 216, 216, 191),
        .Cursor = FromRgb(255, 255, 255, 153),
        .TooltipBg = FromRgb(45, 45, 48, 242),
        .TooltipBorder = FromRgb(100, 100, 110),
        .LoadingBg = FromRgb(18, 18, 22),
        .AudioBg = FromRgb(30, 35, 43),
        .AudioCenterLine = FromRgb(255, 255, 255, 38),
        .AudioSeparator = FromRgb(255, 255, 255, 25),
        .AudioBorder = FromRgb(255, 255, 255, 38),
        .AudioLabelBg = FromRgb(0, 0, 0, 153),
        .AudioLabelBorder = FromRgb(255, 255, 255, 38),
        .AudioLabelText = FromRgb(255, 255, 255, 204),
        .PlayheadBorder = FromRgb(255, 255, 255, 127),
        .MarkerBorder = FromRgb(102, 102, 114)
    }

    ' Константы светлой темы
    Public ReadOnly Light As New PaletteColors With {
        .Background = FromRgb(243, 243, 243),
        .Text = FromRgb(0, 0, 0),
        .Outline = FromRgb(0, 0, 0, 20),
        .Placeholder = FromRgb(200, 200, 200, 127),
        .TickLong = FromRgb(0, 0, 0, 178),
        .TickShort = FromRgb(0, 0, 0, 127),
        .Cursor = FromRgb(0, 0, 0, 102),
        .TooltipBg = FromRgb(245, 245, 245, 242),
        .TooltipBorder = FromRgb(180, 180, 180),
        .LoadingBg = FromRgb(230, 230, 230),
        .AudioBg = FromRgb(234, 234, 242),
        .AudioCenterLine = FromRgb(0, 0, 0, 38),
        .AudioSeparator = FromRgb(0, 0, 0, 25),
        .AudioBorder = FromRgb(0, 0, 0, 25),
        .AudioLabelBg = FromRgb(255, 255, 255, 153),
        .AudioLabelBorder = FromRgb(0, 0, 0, 51),
        .AudioLabelText = FromRgb(0, 0, 0, 204),
        .PlayheadBorder = FromRgb(0, 0, 0, 204),
        .MarkerBorder = FromRgb(25, 25, 38)
    }

    ' Метод для быстрого получения текущего набора цветов
    Public Function GetCurrent(isDark As Boolean) As PaletteColors
        Return If(isDark, Dark, Light)
    End Function
End Module