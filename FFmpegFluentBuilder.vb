Option Strict On
Option Explicit On

Imports System.Globalization
Imports System.Text

Public Class FFmpegFluentBuilder
    Private ReadOnly _globalArgs As New List(Of String)()
    Private ReadOnly _inputArgs As New List(Of String)()
    Private ReadOnly _inputs As New List(Of String)()
    Private ReadOnly _videoArgs As New List(Of String)()
    Private ReadOnly _audioArgs As New List(Of String)()
    Private ReadOnly _filters As New List(Of String)()
    Private ReadOnly _outputArgs As New List(Of String)()
    Private _outputFile As String = String.Empty

    Public Shared Function FormatTime(ts As TimeSpan) As String
        If ts < TimeSpan.Zero Then ts = TimeSpan.Zero
        Return String.Format(CultureInfo.InvariantCulture, "{0:D2}:{1:D2}:{2:D2}.{3:D3}", CInt(Math.Floor(ts.TotalHours)), ts.Minutes, ts.Seconds, ts.Milliseconds)
    End Function

    Public Function HideBannerAndErrors() As FFmpegFluentBuilder
        _globalArgs.Add("-hide_banner -loglevel error")
        Return Me
    End Function

    Public Function Overwrite() As FFmpegFluentBuilder
        _globalArgs.Add("-y")
        Return Me
    End Function

    Public Function Threads(count As Integer) As FFmpegFluentBuilder
        _globalArgs.Add($"-threads {count}")
        Return Me
    End Function

    Public Function AddStats() As FFmpegFluentBuilder
        _globalArgs.Add("-stats")
        Return Me
    End Function

    Public Function InputSeek(ts As TimeSpan) As FFmpegFluentBuilder
        If ts > TimeSpan.Zero Then _inputArgs.Add($"-ss {FormatTime(ts)}")
        Return Me
    End Function

    Public Function FastSeekNoKey(durationSeconds As Double) As FFmpegFluentBuilder
        If durationSeconds > 60 Then _inputArgs.Add("-skip_frame nokey")
        Return Me
    End Function

    Public Function HardwareAccelAuto() As FFmpegFluentBuilder
        _inputArgs.Add("-hwaccel auto")
        Return Me
    End Function

    Public Function HardwareAccel(hwaccelType As String) As FFmpegFluentBuilder
        If Not String.IsNullOrEmpty(hwaccelType) Then _inputArgs.Add($"-hwaccel {hwaccelType} -hwaccel_device 0")
        Return Me
    End Function

    Public Function AddCustomInputArg(arg As String) As FFmpegFluentBuilder
        If Not String.IsNullOrEmpty(arg) Then _inputArgs.Add(arg)
        Return Me
    End Function

    Public Function GeneratePts() As FFmpegFluentBuilder
        _inputArgs.Add("-fflags +genpts")
        Return Me
    End Function

    Public Function AddInput(filePath As String) As FFmpegFluentBuilder
        If _inputArgs.Count > 0 Then
            _inputs.Add(String.Join(" "c, _inputArgs) & " " & $"-i ""{filePath}""")
            _inputArgs.Clear()
        Else
            _inputs.Add($"-i ""{filePath}""")
        End If
        Return Me
    End Function

    Public Function OutputDuration(ts As TimeSpan) As FFmpegFluentBuilder
        If ts > TimeSpan.Zero Then _outputArgs.Add($"-t {FormatTime(ts)}")
        Return Me
    End Function

    Public Function OutputDuration(seconds As Double) As FFmpegFluentBuilder
        If seconds > 0 Then _outputArgs.Add($"-t {seconds.ToString("F3", CultureInfo.InvariantCulture)}")
        Return Me
    End Function

    Public Function VideoCodec(codec As String) As FFmpegFluentBuilder
        _videoArgs.Add($"-c:v {codec}")
        Return Me
    End Function

    Public Function AudioCodec(codec As String) As FFmpegFluentBuilder
        _audioArgs.Add($"-c:a {codec}")
        Return Me
    End Function

    Public Function DisableVideo() As FFmpegFluentBuilder
        _videoArgs.Add("-vn")
        Return Me
    End Function

    Public Function DisableAudio() As FFmpegFluentBuilder
        _audioArgs.Add("-an")
        Return Me
    End Function

    Public Function ComplexFilter(filter As String) As FFmpegFluentBuilder
        If Not String.IsNullOrEmpty(filter) Then _filters.Add($"-filter_complex ""{filter}""")
        Return Me
    End Function

    Public Function Map(mapping As String) As FFmpegFluentBuilder
        _outputArgs.Add($"-map ""{mapping}""")
        Return Me
    End Function

    Public Function RemoveMetadata() As FFmpegFluentBuilder
        _outputArgs.Add("-map_metadata -1 -map_chapters -1")
        Return Me
    End Function

    Public Function Format(formatName As String) As FFmpegFluentBuilder
        _outputArgs.Add($"-f {formatName}")
        Return Me
    End Function

    Public Function AddCustomOutputArg(arg As String) As FFmpegFluentBuilder
        If Not String.IsNullOrEmpty(arg) Then _outputArgs.Add(arg)
        Return Me
    End Function

    Public Function SetOutput(filePath As String) As FFmpegFluentBuilder
        _outputFile = $"""{filePath}"""
        Return Me
    End Function

    Public Function VideoBitrate(bitrate As String) As FFmpegFluentBuilder
        _videoArgs.Add($"-b:v {bitrate}")
        Return Me
    End Function

    Public Function AudioBitrate(bitrate As String) As FFmpegFluentBuilder
        _audioArgs.Add($"-b:a {bitrate}")
        Return Me
    End Function

    Public Function VideoQualityOrPreset(optionName As String, value As String) As FFmpegFluentBuilder
        If Not String.IsNullOrEmpty(optionName) AndAlso Not String.IsNullOrEmpty(value) Then _videoArgs.Add($"{optionName} {value}")
        Return Me
    End Function

    Public Function ExtractFrames(count As Integer) As FFmpegFluentBuilder
        _videoArgs.Add($"-frames:v {count}")
        Return Me
    End Function

    Public Function VideoFilter(filter As String) As FFmpegFluentBuilder
        If Not String.IsNullOrEmpty(filter) Then _filters.Add($"-vf ""{filter}""")
        Return Me
    End Function

    Public Function Build() As String
        Dim sb As New StringBuilder()
        If _globalArgs.Count > 0 Then sb.Append(String.Join(" ", _globalArgs)).Append(" "c)
        If _inputs.Count > 0 Then sb.Append(String.Join(" ", _inputs)).Append(" "c)
        If _filters.Count > 0 Then sb.Append(String.Join(" ", _filters)).Append(" "c)
        If _videoArgs.Count > 0 Then sb.Append(String.Join(" ", _videoArgs)).Append(" "c)
        If _audioArgs.Count > 0 Then sb.Append(String.Join(" ", _audioArgs)).Append(" "c)
        If _outputArgs.Count > 0 Then sb.Append(String.Join(" ", _outputArgs)).Append(" "c)
        If Not String.IsNullOrEmpty(_outputFile) Then sb.Append(_outputFile)
        Return sb.ToString().Trim()
    End Function
End Class