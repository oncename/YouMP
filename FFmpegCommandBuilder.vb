Option Strict On
Option Explicit On

Imports System.Collections.Generic
Imports System.Globalization
Imports System.Linq
Imports System.Text

Public Structure BuildResult
    Public Property Arguments As String
    Public Property ActualEncoder As String
    Public Property IsFallbackApplied As Boolean
End Structure

Public Structure FFmpegCutRegion
    Public ReadOnly StartTime As TimeSpan
    Public ReadOnly EndTime As TimeSpan
    Public Sub New(startTime As TimeSpan, endTime As TimeSpan)
        Me.StartTime = startTime
        Me.EndTime = endTime
    End Sub
End Structure

Public Structure KeepRegion
    Public ReadOnly StartTime As TimeSpan
    Public ReadOnly EndTime As TimeSpan
    Public Sub New(startTime As TimeSpan, endTime As TimeSpan)
        Me.StartTime = startTime
        Me.EndTime = endTime
    End Sub
End Structure

Partial Public Class FFmpegCommandBuilder

    Public Shared Function FormatTimeForFFmpeg(ts As TimeSpan) As String
        Return FFmpegFluentBuilder.FormatTime(ts)
    End Function

    Public Shared Function GetOutputExtension(format As String) As String
        If String.IsNullOrWhiteSpace(format) Then Return "mp4"

        Dim upperFormat As String = format.Trim().ToUpperInvariant()

        If upperFormat.Contains("MP4") Then Return "mp4"
        If upperFormat.Contains("MKV") Then Return "mkv"
        If upperFormat.Contains("AVI") Then Return "avi"
        If upperFormat.Contains("MOV") Then Return "mov"
        If upperFormat.Contains("FLV") Then Return "flv"
        If upperFormat.Contains("WEBM") Then Return "webm"
        If upperFormat.Contains("WMV") Then Return "wmv"
        If upperFormat.Contains("M4V") Then Return "m4v"
        If upperFormat.Contains("TS") Then Return "ts"
        If upperFormat.Contains("MP3") Then Return "mp3"
        If upperFormat.Contains("WAV") Then Return "wav"
        If upperFormat.Contains("AAC") Then Return "aac"
        If upperFormat.Contains("FLAC") Then Return "flac"
        If upperFormat.Contains("OGG") Then Return "ogg"
        If upperFormat.Contains("OPUS") Then Return "opus"
        If upperFormat.Contains("AC3") Then Return "ac3"
        If upperFormat.Contains("GIF") Then Return "gif"
        If upperFormat.Contains("WEBP") Then Return "webp"
        If upperFormat.Contains("PNG") Then Return "png"
        If upperFormat.Contains("AVIF") Then Return "avif"
        If upperFormat.Contains("JPEG XL") OrElse upperFormat.Contains("JXL") Then Return "jxl"
        If upperFormat.Contains("JPG") OrElse upperFormat.Contains("JPEG") Then Return "jpg"
        If upperFormat.Contains("BMP") Then Return "bmp"

        Return "mp4"
    End Function

    Private Shared Function IsLegacyCodec(enc As String) As Boolean
        Return {"mpeg4", "libxvid", "flv", "wmv2", "wmv1", "prores_ks", "mpeg2video", "dnxhd", "cfhd", "libvvenc"}.Contains(enc)
    End Function

    Private Shared Function DetermineVideoEncoder(videoCodec As String, safeFormat As String, isNvidiaGpu As Boolean, isAmdGpu As Boolean) As String
        Dim safeVideoCodecUpper As String = If(videoCodec, String.Empty).Trim().ToUpperInvariant()
        If safeVideoCodecUpper.Contains("COPY") Then Return "copy"
        If safeVideoCodecUpper.Contains("H.266") OrElse safeVideoCodecUpper.Contains("LIBVVENC") OrElse safeVideoCodecUpper.Contains("VWC") Then Return "libvvenc"
        If safeVideoCodecUpper.Contains("H.265") OrElse safeVideoCodecUpper.Contains("HEVC") OrElse safeVideoCodecUpper.Contains("LIBX265") Then Return If(isNvidiaGpu, "hevc_nvenc", If(isAmdGpu, "hevc_amf", "libx265"))
        If safeVideoCodecUpper.Contains("H.264") OrElse safeVideoCodecUpper.Contains("LIBX264") Then Return If(isNvidiaGpu, "h264_nvenc", If(isAmdGpu, "h264_amf", "libx264"))
        If safeVideoCodecUpper.Contains("AV1") OrElse safeVideoCodecUpper.Contains("LIBSVTAV1") Then Return If(isNvidiaGpu, "av1_nvenc", If(isAmdGpu, "av1_amf", "libsvtav1"))
        If safeVideoCodecUpper.Contains("VP9") OrElse safeVideoCodecUpper.Contains("LIBVPX-VP9") Then Return "libvpx-vp9"
        If safeVideoCodecUpper.Contains("VP8") OrElse safeVideoCodecUpper.Contains("LIBVPX") Then Return "libvpx"
        If safeVideoCodecUpper.Contains("MPEG-4") OrElse safeVideoCodecUpper.Contains("MPEG4") Then Return "mpeg4"
        If safeVideoCodecUpper.Contains("MPEG-2") OrElse safeVideoCodecUpper.Contains("MPEG2") Then Return "mpeg2video"
        If safeVideoCodecUpper.Contains("XVID") OrElse safeVideoCodecUpper.Contains("DIVX") OrElse safeVideoCodecUpper.Contains("LIBXVID") Then Return "libxvid"
        If safeVideoCodecUpper.Contains("FLV1") OrElse safeVideoCodecUpper.Contains("FLV") Then Return "flv"
        If safeVideoCodecUpper.Contains("WMV1") Then Return "wmv1"
        If safeVideoCodecUpper.Contains("WMV2") Then Return "wmv2"
        If safeVideoCodecUpper.Contains("PRORES") OrElse safeVideoCodecUpper.Contains("PRORES_KS") Then Return "prores_ks"
        If safeVideoCodecUpper.Contains("DNXHR") OrElse safeVideoCodecUpper.Contains("DNXHD") Then Return "dnxhd"
        If safeVideoCodecUpper.Contains("CINEFORM") OrElse safeVideoCodecUpper.Contains("CFHD") Then Return "cfhd"

        Dim isHevcRequested As Boolean = safeVideoCodecUpper.Contains("H.265") OrElse safeVideoCodecUpper.Contains("HEVC")
        Select Case safeFormat
            Case "WebM (VP9/AV1)" : Return "libvpx-vp9"
            Case "WMV (Windows Media Video)" : Return "wmv2"
            Case "FLV (Flash Video)" : Return "flv"
            Case "AVI (Audio Video Interleave)" : Return "libxvid"
            Case Else
                Return If(isHevcRequested, If(isNvidiaGpu, "hevc_nvenc", If(isAmdGpu, "hevc_amf", "libx265")), If(isNvidiaGpu, "h264_nvenc", If(isAmdGpu, "h264_amf", "libx264")))
        End Select
    End Function

    Private Shared Function DetermineImageEncoder(safeFormat As String) As String
        Select Case safeFormat
            Case "Image GIF" : Return "gif"
            Case "Image WebP" : Return "libwebp"
            Case "Image PNG" : Return "png"
            Case "Image JPG" : Return "mjpeg"
            Case "Image AVIF" : Return "libsvtav1"
            Case "Image JPEG XL" : Return "libjxl"
            Case "Image BMP" : Return "bmp"
            Case Else : Return "mjpeg"
        End Select
    End Function

    Private Shared Function BuildQualityArgs(enc As String, compLevel As String, isNvidiaGpu As Boolean, isAmdGpu As Boolean) As String
        If IsLegacyCodec(enc) Then
            If enc = "dnxhd" Then Return "-profile:v dnxhr_hq"
            ' Для устаревших кодеков (mpeg4, xvid, wmv) используем qscale:v
            Dim qVal As String = If(compLevel = "Minimal", "2", If(compLevel = "Low", "4", If(compLevel = "Medium", "8", If(compLevel = "High", "15", "25"))))
            Return "-qscale:v " & qVal
        End If

        Dim maxRate As String = If(compLevel = "Minimal", "30M", If(compLevel = "Low", "20M", If(compLevel = "Medium", "12M", If(compLevel = "High", "6M", "3M"))))
        Dim bufSize As String = If(compLevel = "Minimal", "60M", If(compLevel = "Low", "40M", If(compLevel = "Medium", "24M", If(compLevel = "High", "12M", "6M"))))

        If isNvidiaGpu AndAlso (enc = "h264_nvenc" OrElse enc = "hevc_nvenc" OrElse enc = "av1_nvenc") Then
            Dim pVal As String = If(compLevel = "Minimal", "p7", If(compLevel = "Low", "p6", If(compLevel = "Medium", "p4", If(compLevel = "High", "p3", "p1"))))
            Dim cqVal As String = If(compLevel = "Minimal", "18", If(compLevel = "Low", "23", If(compLevel = "Medium", "28", If(compLevel = "High", "33", "38"))))

            Dim rcArgs As String = $"-rc vbr -cq {cqVal} -b:v 0 -maxrate {maxRate} -bufsize {bufSize}"

            If enc = "av1_nvenc" Then
                Return $"-preset {pVal} -tune hq {rcArgs} -spatial-aq 1"
            Else
                Return $"-preset {pVal} -tune hq {rcArgs} -multipass 2 -spatial-aq 1"
            End If

        ElseIf isAmdGpu AndAlso (enc = "h264_amf" OrElse enc = "hevc_amf" OrElse enc = "av1_amf") Then
            Dim qVal As String = If(compLevel = "Minimal" OrElse compLevel = "Low", "quality", If(compLevel = "Medium", "balanced", "speed"))
            Dim qpVal As String = If(compLevel = "Minimal", "18", If(compLevel = "Low", "23", If(compLevel = "Medium", "28", If(compLevel = "High", "33", "38"))))
            Return $"-quality {qVal} -rc cqp -qp_i {qpVal} -qp_p {qpVal}"

        ElseIf enc = "libsvtav1" Then
            Dim presetVal As String = If(compLevel = "Minimal", "4", If(compLevel = "Low", "6", If(compLevel = "Medium", "8", If(compLevel = "High", "10", "12"))))
            Dim crfVal As String = If(compLevel = "Minimal", "22", If(compLevel = "Low", "26", If(compLevel = "Medium", "30", If(compLevel = "High", "34", "40"))))
            Return $"-preset {presetVal} -crf {crfVal} -strict experimental"

        ElseIf enc = "libvpx-vp9" Then
            Dim cpuUsed As String = If(compLevel = "Minimal", "1", If(compLevel = "Low", "2", If(compLevel = "Medium", "3", If(compLevel = "High", "4", "5"))))
            Dim crfVal As String = If(compLevel = "Minimal", "20", If(compLevel = "Low", "26", If(compLevel = "Medium", "32", If(compLevel = "High", "38", "45"))))
            Return $"-cpu-used {cpuUsed} -crf {crfVal} -b:v {maxRate} -auto-alt-ref 0 -pix_fmt yuv420p"

        ElseIf enc = "libvpx" Then
            ' ИСПРАВЛЕНИЕ: Выделенный блок для VP8 (libvpx), предотвращающий подстановку несовместимого -preset
            Dim cpuUsed As String = If(compLevel = "Minimal", "1", If(compLevel = "Low", "2", If(compLevel = "Medium", "3", If(compLevel = "High", "4", "5"))))
            Dim crfVal As String = If(compLevel = "Minimal", "10", If(compLevel = "Low", "15", If(compLevel = "Medium", "22", If(compLevel = "High", "30", "40"))))
            Return $"-cpu-used {cpuUsed} -crf {crfVal} -b:v {maxRate} -auto-alt-ref 0 -pix_fmt yuv420p"

        Else
            ' Общий блок для libx264, libx265
            Dim presetVal As String = If(compLevel = "Minimal", "slow", If(compLevel = "Low", "medium", If(compLevel = "High", "fast", If(compLevel = "Maximum", "veryfast", "medium"))))
            Dim crfVal As String = If(compLevel = "Minimal", "18", If(compLevel = "Low", "23", If(compLevel = "Medium", "28", If(compLevel = "High", "33", "38"))))
            Return $"-preset {presetVal} -crf {crfVal} -maxrate {maxRate} -bufsize {bufSize}"
        End If
    End Function

    Private Shared Function BuildImageQualityArgs(builder As FFmpegFluentBuilder, enc As String, safeCompLevel As String) As String
        Select Case enc
            Case "libwebp"
                Dim webpArgs As String = If(safeCompLevel = "Minimal", "-qscale 95", If(safeCompLevel = "Low", "-qscale 80", If(safeCompLevel = "Medium", "-qscale 60", If(safeCompLevel = "High", "-qscale 40", "-qscale 15"))))
                builder.AddCustomOutputArg("-loop 0")
                Return webpArgs
            Case "gif"
                builder.AddCustomOutputArg("-loop 0")
                Return String.Empty
            Case "libsvtav1"
                Dim crfAVIF As String = If(safeCompLevel = "Minimal", "15", If(safeCompLevel = "Low", "22", If(safeCompLevel = "Medium", "30", If(safeCompLevel = "High", "40", "50"))))
                builder.Format("avif")
                Return "-preset 4 -crf " & crfAVIF & " -pix_fmt yuv420p -strict experimental"
            Case "libjxl"
                Dim qJXL As String = If(safeCompLevel = "Minimal", "100", If(safeCompLevel = "Low", "90", If(safeCompLevel = "Medium", "75", If(safeCompLevel = "High", "50", "30"))))
                Return "-q:v " & qJXL & " -strict experimental"
            Case "png"
                Dim compPNG As String = If(safeCompLevel = "Minimal", "1", If(safeCompLevel = "Low", "3", If(safeCompLevel = "Medium", "5", If(safeCompLevel = "High", "7", "9"))))
                Return "-compression_level " & compPNG
            Case "mjpeg"
                Dim qJPG As String = If(safeCompLevel = "Minimal", "2", If(safeCompLevel = "Low", "5", If(safeCompLevel = "Medium", "10", If(safeCompLevel = "High", "15", "25"))))
                Return "-q:v " & qJPG
            Case Else
                Return String.Empty
        End Select
    End Function

    Private Shared Sub ApplyGifAndWebpFilters(builder As FFmpegFluentBuilder, enc As String, safeCompLevel As String, vfStr As String)
        If enc = "gif" Then
            Dim maxColors As String = "256"
            Dim dither As String = ""
            Dim fpsFilter As String = ""

            Select Case safeCompLevel
                Case "Minimal" : fpsFilter = "fps=24" : maxColors = "256"
                Case "Low" : fpsFilter = "fps=20" : maxColors = "256" : dither = "=dither=bayer:bayer_scale=3"
                Case "Medium" : fpsFilter = "fps=15" : maxColors = "128" : dither = "=dither=bayer:bayer_scale=5"
                Case "High" : fpsFilter = "fps=10" : maxColors = "64" : dither = "=dither=none:diff_mode=rectangle"
                Case "Maximum" : fpsFilter = "fps=8" : maxColors = "32" : dither = "=dither=none:diff_mode=rectangle"
            End Select

            Dim filtersList As New List(Of String)()
            If Not String.IsNullOrEmpty(vfStr) Then filtersList.Add(vfStr)
            If Not String.IsNullOrEmpty(fpsFilter) Then filtersList.Add(fpsFilter)

            Dim combinedPreFilters As String = String.Join(",", filtersList)
            Dim paletteFilter As String = "split[s0][s1];[s0]palettegen=max_colors=" & maxColors & ":reserve_transparent=1[p];[s1][p]paletteuse" & dither
            If String.IsNullOrEmpty(combinedPreFilters) Then
                builder.VideoFilter(paletteFilter)
            Else
                builder.VideoFilter(combinedPreFilters & "," & paletteFilter)
            End If
        ElseIf Not String.IsNullOrEmpty(vfStr) Then
            builder.VideoFilter(vfStr)
        End If
    End Sub

    Private Shared Sub ApplyAudioCodecSettings(builder As FFmpegFluentBuilder, format As String, compLevel As String)
        Dim ext As String = GetOutputExtension(format)
        Dim aBitrate As String = "192k"

        If Not String.IsNullOrEmpty(compLevel) AndAlso compLevel.Contains("kbps", StringComparison.InvariantCultureIgnoreCase) Then
            aBitrate = compLevel.ToLowerInvariant().Replace("kbps", "").Trim() & "k"
        Else
            Select Case compLevel
                Case "Minimal" : aBitrate = "320k"
                Case "Low" : aBitrate = "256k"
                Case "Medium" : aBitrate = "192k"
                Case "High" : aBitrate = "128k"
                Case "Maximum" : aBitrate = "96k"
            End Select
        End If

        Select Case ext
            Case "mp3"
                builder.AudioCodec("libmp3lame").AudioBitrate(aBitrate).Format("mp3")
            Case "wav"
                builder.AudioCodec("pcm_s16le").Format("wav")
            Case "aac", "m4a"
                builder.AudioCodec("aac").AudioBitrate(aBitrate).Format("adts")
            Case "flac"
                builder.AudioCodec("flac").Format("flac")
            Case "ogg"
                builder.AudioCodec("libvorbis").Format("ogg")
            Case "opus"
                builder.AudioCodec("libopus").AudioBitrate(aBitrate).Format("opus")
            Case "webm"
                ' ИСПРАВЛЕНИЕ: Жестко ограничиваем Opus. 
                ' Кодек Opus не приемлет экстремально высокие битрейты для моноканалов (например, 320k).
                ' Учитывая его эффективность, 192k гарантирует прозрачное качество без падения с ошибкой -22.
                builder.AudioCodec("libopus").AudioBitrate("192k")
            Case "ac3"
                builder.AudioCodec("ac3").AudioBitrate(aBitrate).Format("ac3")
            Case Else
                builder.AudioCodec("aac").AudioBitrate(aBitrate)
        End Select
    End Sub

    Private Shared Function ApplyVideoEffectsToFilter(sb As StringBuilder, inputNode As String, videoFadeIn As TimeSpan, videoFadeOut As TimeSpan, totalDurationSec As Double) As String
        Dim videoFilters As New List(Of String)()

        If videoFadeIn > TimeSpan.Zero Then
            videoFilters.Add("fade=t=in:st=0:d=" & videoFadeIn.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture) & ":color=black")
        End If

        If videoFadeOut > TimeSpan.Zero Then
            Dim fadeOutStart As Double = totalDurationSec - videoFadeOut.TotalSeconds
            If fadeOutStart < 0 Then fadeOutStart = 0
            videoFilters.Add("fade=t=out:st=" & fadeOutStart.ToString("0.000", CultureInfo.InvariantCulture) & ":d=" & videoFadeOut.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture) & ":color=black")
        End If

        If videoFilters.Count = 0 Then Return inputNode

        Dim filterStr As String = String.Join(",", videoFilters)
        Dim outputNode As String = "[processed_v]"

        If sb.Length > 0 AndAlso sb(sb.Length - 1) <> ";"c Then
            sb.Append(";"c)
        End If

        sb.Append(inputNode)
        sb.Append(filterStr)
        sb.Append(outputNode)

        Return outputNode
    End Function

    Private Shared Function ApplyAudioEffectsToFilter(sb As StringBuilder, inputNode As String, fadeIn As TimeSpan, fadeOut As TimeSpan, totalDurationSec As Double, trackVolume As Single) As String
        Dim audioFilters As New List(Of String)()
        If trackVolume <> 1.0F Then
            audioFilters.Add("volume=" & trackVolume.ToString(CultureInfo.InvariantCulture))
        End If
        If fadeIn > TimeSpan.Zero Then
            audioFilters.Add("afade=t=in:st=0:d=" & fadeIn.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture))
        End If
        If fadeOut > TimeSpan.Zero Then
            Dim fadeOutStart As Double = totalDurationSec - fadeOut.TotalSeconds
            If fadeOutStart < 0 Then fadeOutStart = 0
            audioFilters.Add("afade=t=out:st=" & fadeOutStart.ToString("0.000", CultureInfo.InvariantCulture) & ":d=" & fadeOut.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture))
        End If

        If audioFilters.Count = 0 Then Return inputNode

        Dim filterStr As String = String.Join(",", audioFilters)
        Dim outputNode As String = "[processed_a]"

        If sb.Length > 0 AndAlso sb(sb.Length - 1) <> ";"c Then
            sb.Append(";"c)
        End If

        sb.Append(inputNode)
        sb.Append(filterStr)
        sb.Append(outputNode)

        Return outputNode
    End Function

    Private Shared Function BuildAudioArgs(builder As FFmpegFluentBuilder, output As String, format As String, compLevel As String, actualDuration As TimeSpan, actualAudioReplaced As Boolean, trackVolume As Single, fadeIn As TimeSpan, fadeOut As TimeSpan) As BuildResult
        Dim result As New BuildResult()

        builder.DisableVideo().RemoveMetadata()

        If actualAudioReplaced Then
            builder.Map("1:a:0?")
        Else
            builder.Map("0:a:0?")
        End If

        ApplyAudioCodecSettings(builder, format, compLevel)

        Dim filters As New List(Of String)()
        If trackVolume <> 1.0F Then
            filters.Add("volume=" & trackVolume.ToString(CultureInfo.InvariantCulture))
        End If
        If fadeIn > TimeSpan.Zero Then
            filters.Add("afade=t=in:st=0:d=" & fadeIn.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture))
        End If
        If fadeOut > TimeSpan.Zero Then
            Dim fadeOutStart As Double = actualDuration.TotalSeconds - fadeOut.TotalSeconds
            If fadeOutStart < 0 Then fadeOutStart = 0
            filters.Add("afade=t=out:st=" & fadeOutStart.ToString("0.000", CultureInfo.InvariantCulture) & ":d=" & fadeOut.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture))
        End If

        If filters.Count > 0 Then
            builder.AddCustomOutputArg("-af """ & String.Join(",", filters) & """")
        End If

        If actualDuration.TotalMilliseconds > 0 Then builder.OutputDuration(actualDuration)

        result.Arguments = builder.SetOutput(output).Build()
        result.ActualEncoder = "Audio"
        Return result
    End Function

    Private Shared Function BuildImageArgs(builder As FFmpegFluentBuilder, output As String, safeFormat As String, compLevel As String, targetW As Integer, targetH As Integer, cropW As Integer, cropH As Integer, cropX As Integer, cropY As Integer) As BuildResult
        Dim result As New BuildResult()
        Dim ext As String = GetOutputExtension(safeFormat)

        Dim filters As New List(Of String)()
        If cropW > 0 AndAlso cropH > 0 Then
            filters.Add("crop=" & cropW & ":" & cropH & ":" & cropX & ":" & cropY)
        End If
        If targetW > 0 AndAlso targetH > 0 Then
            filters.Add("scale=" & targetW & ":" & targetH & ":force_original_aspect_ratio=decrease,pad=" & targetW & ":" & targetH & ":(ow-iw)/2:(oh-ih)/2:black")
        End If

        Dim isAnimatedImage As Boolean = safeFormat = "Image GIF" OrElse safeFormat = "Image WebP"
        If Not isAnimatedImage Then
            builder.ExtractFrames(1)
        End If

        builder.RemoveMetadata()

        Dim enc As String = DetermineImageEncoder(safeFormat)
        builder.VideoCodec(enc)

        Dim qualityArgs As String = BuildImageQualityArgs(builder, enc, compLevel)
        If Not String.IsNullOrEmpty(qualityArgs) Then
            builder.AddCustomOutputArg(qualityArgs)
        End If

        ApplyGifAndWebpFilters(builder, enc, compLevel, String.Join(",", filters))

        result.ActualEncoder = enc
        result.Arguments = builder.SetOutput(output).Build()
        Return result
    End Function

    Private Shared Function BuildVideoArgs(builder As FFmpegFluentBuilder, output As String, safeFormat As String, videoCodec As String, safeCompLevel As String, actualDuration As TimeSpan, hasAudio As Boolean, isNvidiaGpu As Boolean, isAmdGpu As Boolean, targetW As Integer, targetH As Integer, cropW As Integer, cropH As Integer, cropX As Integer, cropY As Integer, isAudioReplaced As Boolean, trackVolume As Single, fadeIn As TimeSpan, fadeOut As TimeSpan, videoFadeIn As TimeSpan, videoFadeOut As TimeSpan) As BuildResult
        Dim result As New BuildResult()
        Dim threads As Integer = Environment.ProcessorCount
        Dim ext As String = GetOutputExtension(safeFormat).ToLowerInvariant()

        If actualDuration.TotalMilliseconds > 0 Then builder.OutputDuration(actualDuration)
        builder.RemoveMetadata()

        Dim enc As String = DetermineVideoEncoder(videoCodec, safeFormat, isNvidiaGpu, isAmdGpu)

        Dim videoTransformRequested As Boolean = (cropW > 0 AndAlso cropH > 0) OrElse (targetW > 0 AndAlso targetH > 0) OrElse (videoFadeIn > TimeSpan.Zero) OrElse (videoFadeOut > TimeSpan.Zero)
        If enc = "copy" AndAlso videoTransformRequested Then
            enc = If(isNvidiaGpu, "h264_nvenc", If(isAmdGpu, "h264_amf", "libx264"))
            result.IsFallbackApplied = True
        End If

        Dim qualityArgs As String = BuildQualityArgs(enc, safeCompLevel, isNvidiaGpu, isAmdGpu)

        Dim videoFilters As New List(Of String)()
        If cropW > 0 AndAlso cropH > 0 Then
            videoFilters.Add("crop=" & cropW & ":" & cropH & ":" & cropX & ":" & cropY)
        End If
        If targetW > 0 AndAlso targetH > 0 Then
            videoFilters.Add("scale=" & targetW & ":" & targetH & ":force_original_aspect_ratio=decrease,pad=" & targetW & ":" & targetH & ":(ow-iw)/2:(oh-ih)/2:black")
        End If

        If videoFadeIn > TimeSpan.Zero Then
            videoFilters.Add("fade=t=in:st=0:d=" & videoFadeIn.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture) & ":color=black")
        End If
        If videoFadeOut > TimeSpan.Zero Then
            Dim fadeOutStart As Double = actualDuration.TotalSeconds - videoFadeOut.TotalSeconds
            If fadeOutStart < 0 Then fadeOutStart = 0
            videoFilters.Add("fade=t=out:st=" & fadeOutStart.ToString("0.000", CultureInfo.InvariantCulture) & ":d=" & videoFadeOut.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture) & ":color=black")
        End If

        Dim vfStr As String = String.Join(",", videoFilters)

        If enc = "gif" OrElse enc = "libwebp" Then
            ApplyGifAndWebpFilters(builder, enc, safeCompLevel, vfStr)
        ElseIf videoFilters.Count > 0 Then
            builder.VideoFilter(vfStr)
        End If

        If isAudioReplaced Then builder.AddCustomOutputArg("-map 0:v:0 -map 1:a:0?")

        If enc = "copy" Then
            builder.AddCustomOutputArg("-avoid_negative_ts make_zero -max_muxing_queue_size 1024")
        Else
            Dim forceYuv420p As Boolean = ext <> "webm" AndAlso ext <> "gif"
            If forceYuv420p Then
                builder.AddCustomOutputArg("-avoid_negative_ts make_zero -max_muxing_queue_size 1024 -pix_fmt yuv420p")
            Else
                builder.AddCustomOutputArg("-avoid_negative_ts make_zero -max_muxing_queue_size 1024")
            End If
        End If

        builder.VideoCodec(enc)
        If (ext = "mp4" OrElse ext = "mov" OrElse ext = "m4v") AndAlso (enc = "libx265" OrElse enc = "hevc_nvenc" OrElse enc = "hevc_amf") Then
            builder.AddCustomOutputArg("-tag:v hvc1")
        End If

        If Not String.IsNullOrEmpty(qualityArgs) Then builder.AddCustomOutputArg(qualityArgs)

        If hasAudio Then
            ApplyAudioCodecSettings(builder, safeFormat, safeCompLevel)

            Dim audioFilters As New List(Of String)()
            If trackVolume <> 1.0F Then
                audioFilters.Add("volume=" & trackVolume.ToString(CultureInfo.InvariantCulture))
            End If
            If fadeIn > TimeSpan.Zero Then
                audioFilters.Add("afade=t=in:st=0:d=" & fadeIn.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture))
            End If
            If fadeOut > TimeSpan.Zero Then
                Dim fadeOutStart As Double = actualDuration.TotalSeconds - fadeOut.TotalSeconds
                If fadeOutStart < 0 Then fadeOutStart = 0
                audioFilters.Add("afade=t=out:st=" & fadeOutStart.ToString("0.000", CultureInfo.InvariantCulture) & ":d=" & fadeOut.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture))
            End If

            If audioFilters.Count > 0 Then
                builder.AddCustomOutputArg("-af """ & String.Join(",", audioFilters) & """")
            End If
        Else
            builder.DisableAudio()
        End If

        builder.Threads(threads)
        result.ActualEncoder = enc
        result.Arguments = builder.SetOutput(output).Build()
        Return result
    End Function

    Public Shared Function BuildFFmpegArguments(input As String, output As String, format As String, videoCodec As String, compressionLevel As String, tStart As TimeSpan, tEnd As TimeSpan, hasAudio As Boolean, cDurCheck As TimeSpan, isNvidiaGpu As Boolean, isAmdGpu As Boolean, targetW As Integer, targetH As Integer, isInputImage As Boolean, cropW As Integer, cropH As Integer, cropX As Integer, cropY As Integer, Optional isAudioReplaced As Boolean = False, Optional externalAudioPath As String = "", Optional trackVolume As Single = 1.0F, Optional fadeIn As TimeSpan = Nothing, Optional fadeOut As TimeSpan = Nothing, Optional videoFadeIn As TimeSpan = Nothing, Optional videoFadeOut As TimeSpan = Nothing) As BuildResult
        Dim safeFormat As String = If(format, String.Empty)
        Dim safeCompLevel As String = If(compressionLevel, "Medium")
        Dim safeVideoCodec As String = If(videoCodec, String.Empty)

        Dim actualAudioReplaced As Boolean = isAudioReplaced AndAlso Not String.IsNullOrWhiteSpace(externalAudioPath)
        Dim effectiveHasAudio As Boolean = hasAudio OrElse actualAudioReplaced

        Dim isAudio As Boolean = safeFormat.StartsWith("Audio", StringComparison.OrdinalIgnoreCase)
        Dim isOutputImage As Boolean = safeFormat.StartsWith("Image", StringComparison.OrdinalIgnoreCase)

        Dim tStartEffective As TimeSpan = tStart
        Dim tEndEffective As TimeSpan = tEnd

        If tStartEffective > tEndEffective Then
            tStartEffective = TimeSpan.Zero
            tEndEffective = If(cDurCheck > TimeSpan.Zero, cDurCheck, TimeSpan.Zero)
        End If

        Dim trimSpan As TimeSpan = If(tEndEffective > tStartEffective, tEndEffective - tStartEffective, TimeSpan.Zero)
        Dim builder As New FFmpegFluentBuilder()

        builder.HideBannerAndErrors().AddStats().Overwrite().InputSeek(tStartEffective)

        If Not isOutputImage AndAlso Not isAudio Then
            If isNvidiaGpu Then
                builder.HardwareAccel("cuda")
            ElseIf isAmdGpu Then
                builder.HardwareAccelAuto()
            End If
        End If

        builder.GeneratePts().AddInput(input)
        If actualAudioReplaced Then
            builder.InputSeek(tStartEffective).AddInput(externalAudioPath)
        End If

        If isAudio Then
            Return BuildAudioArgs(builder, output, safeFormat, safeCompLevel, trimSpan, actualAudioReplaced, trackVolume, fadeIn, fadeOut)
        ElseIf isOutputImage AndAlso isInputImage Then
            Return BuildImageArgs(builder, output, safeFormat, safeCompLevel, targetW, targetH, cropW, cropH, cropX, cropY)
        Else
            Return BuildVideoArgs(builder, output, safeFormat, safeVideoCodec, safeCompLevel, trimSpan, effectiveHasAudio, isNvidiaGpu, isAmdGpu, targetW, targetH, cropW, cropH, cropX, cropY, actualAudioReplaced, trackVolume, fadeIn, fadeOut, videoFadeIn, videoFadeOut)
        End If
    End Function

    Public Shared Function BuildExportCommandWithCuts(
        fullVideoPath As String,
        cutRegions As IReadOnlyList(Of FFmpegCutRegion),
        hasAudio As Boolean,
        selectedFormat As String,
        videoEncoderLabel As String,
        compressionLevel As String,
        outputFilePath As String,
        threadsCount As Integer,
        isNvidiaGpu As Boolean,
        isAmdGpu As Boolean,
        markerStart As TimeSpan,
        markerEnd As TimeSpan,
        targetW As Integer,
        targetH As Integer,
        isInputImage As Boolean,
        cropW As Integer,
        cropH As Integer,
        cropX As Integer,
        cropY As Integer,
        Optional isAudioReplaced As Boolean = False,
        Optional externalAudioPath As String = "",
        Optional audioOffset As TimeSpan = Nothing,
        Optional fadeIn As TimeSpan = Nothing,
        Optional fadeOut As TimeSpan = Nothing,
        Optional trackVolume As Single = 1.0F,
        Optional videoFadeIn As TimeSpan = Nothing,
        Optional videoFadeOut As TimeSpan = Nothing,
        Optional sourceFps As Double = 30.0
    ) As BuildResult

        Dim result As New BuildResult()
        Dim builder As New FFmpegFluentBuilder()
        Dim safeFormat As String = If(selectedFormat, String.Empty)
        Dim isAudioOnly As Boolean = safeFormat.StartsWith("Audio", StringComparison.OrdinalIgnoreCase)
        Dim isOutputImage As Boolean = safeFormat.StartsWith("Image", StringComparison.OrdinalIgnoreCase)
        Dim actualAudioReplaced As Boolean = isAudioReplaced AndAlso Not String.IsNullOrWhiteSpace(externalAudioPath)
        Dim effectiveHasAudio As Boolean = hasAudio OrElse actualAudioReplaced
        Dim actualAudioOffset As TimeSpan = If(audioOffset <> Nothing, audioOffset, TimeSpan.Zero)

        builder.HideBannerAndErrors().AddStats().Overwrite().Threads(threadsCount)

        If isOutputImage AndAlso isInputImage Then
            builder.InputSeek(markerStart).AddInput(fullVideoPath)
            Return BuildImageArgs(builder, outputFilePath, safeFormat, compressionLevel, targetW, targetH, cropW, cropH, cropX, cropY)
        End If

        Dim keepList As New List(Of KeepRegion)()
        If cutRegions IsNot Nothing AndAlso cutRegions.Count > 0 Then
            Dim currentStart As TimeSpan = markerStart
            For Each cut In cutRegions.OrderBy(Function(c) c.StartTime)
                If cut.EndTime <= markerStart OrElse cut.StartTime >= markerEnd Then Continue For
                Dim effCutStart As TimeSpan = If(cut.StartTime < markerStart, markerStart, cut.StartTime)
                Dim effCutEnd As TimeSpan = If(cut.EndTime > markerEnd, markerEnd, cut.EndTime)
                If effCutStart > currentStart Then keepList.Add(New KeepRegion(currentStart, effCutStart))
                If effCutEnd > currentStart Then currentStart = effCutEnd
            Next
            If currentStart < markerEnd Then keepList.Add(New KeepRegion(currentStart, markerEnd))
        Else
            If markerEnd > markerStart Then keepList.Add(New KeepRegion(markerStart, markerEnd))
        End If

        If keepList.Count = 0 Then
            result.Arguments = String.Empty
            Return result
        End If

        If isOutputImage Then
            builder.InputSeek(keepList(0).StartTime).AddInput(fullVideoPath)
            Return BuildImageArgs(builder, outputFilePath, safeFormat, compressionLevel, targetW, targetH, cropW, cropH, cropX, cropY)
        End If

        Dim hasComplexFilters As Boolean = (keepList.Count > 1) OrElse (cropW > 0 AndAlso cropH > 0) OrElse (videoFadeIn > TimeSpan.Zero) OrElse (videoFadeOut > TimeSpan.Zero)

        If Not isAudioOnly AndAlso Not hasComplexFilters Then
            If isNvidiaGpu Then
                builder.HardwareAccel("cuda")
            ElseIf isAmdGpu Then
                builder.HardwareAccelAuto()
            End If
        End If

        builder.GeneratePts().AddInput(fullVideoPath)
        If actualAudioReplaced Then
            builder.AddInput(externalAudioPath)
        End If

        Dim sbFilter As New StringBuilder()
        Dim concatArgs As New StringBuilder()

        Dim vStream As String = "0:v:0"
        Dim aStream As String = If(actualAudioReplaced, "1:a:0", "0:a:0")
        Dim audioSyncNode As String = "aud_sync"

        If effectiveHasAudio OrElse isAudioOnly Then
            If actualAudioOffset.TotalMilliseconds > 0 Then
                Dim delayMs As Integer = CInt(Math.Round(actualAudioOffset.TotalMilliseconds))
                sbFilter.Append("[" & aStream & "]adelay=delays=" & delayMs & ":all=1[" & audioSyncNode & "];")
            ElseIf actualAudioOffset.TotalMilliseconds < 0 Then
                Dim advanceSec As String = Math.Abs(actualAudioOffset.TotalSeconds).ToString("0.000", CultureInfo.InvariantCulture)
                sbFilter.Append("[" & aStream & "]atrim=start=" & advanceSec & ",asetpts=PTS-STARTPTS[" & audioSyncNode & "];")
            Else
                audioSyncNode = aStream
            End If
        End If

        If Not isAudioOnly AndAlso keepList.Count > 1 Then
            Dim vSplitNode As String = String.Join("", Enumerable.Range(0, keepList.Count).Select(Function(i) "[v_sync_" & i & "]"))
            sbFilter.Append("[" & vStream & "]split=" & keepList.Count & vSplitNode & ";")
        End If

        If (effectiveHasAudio OrElse isAudioOnly) AndAlso keepList.Count > 1 Then
            Dim splitNode As String = String.Join("", Enumerable.Range(0, keepList.Count).Select(Function(i) "[aud_sync_" & i & "]"))
            sbFilter.Append("[" & audioSyncNode & "]asplit=" & keepList.Count & splitNode & ";")
        End If

        Dim safeFps As String = If(sourceFps > 0, sourceFps.ToString("0.00", CultureInfo.InvariantCulture), "30.00")

        For i As Integer = 0 To keepList.Count - 1
            Dim exactStart As Double = keepList(i).StartTime.TotalSeconds
            Dim exactEnd As Double = keepList(i).EndTime.TotalSeconds
            Dim exactDur As Double = exactEnd - exactStart

            Dim startSec As String = exactStart.ToString("0.000000", CultureInfo.InvariantCulture)
            Dim endSec As String = exactEnd.ToString("0.000000", CultureInfo.InvariantCulture)
            Dim segDuration As String = exactDur.ToString("0.000000", CultureInfo.InvariantCulture)

            If Not isAudioOnly Then
                Dim currentVideoNode As String = If(keepList.Count > 1, "v_sync_" & i, vStream)

                Dim vNode As String = "[" & currentVideoNode & "]trim=start=" & startSec & ":end=" & endSec & ",setpts=PTS-STARTPTS"
                vNode &= ",fps=" & safeFps & ",format=yuv420p"

                If cropW > 0 AndAlso cropH > 0 Then
                    vNode &= ",crop=" & cropW & ":" & cropH & ":" & cropX & ":" & cropY
                End If
                If targetW > 0 AndAlso targetH > 0 Then
                    vNode &= ",scale=" & targetW & ":" & targetH & ":force_original_aspect_ratio=decrease,pad=" & targetW & ":" & targetH & ":(ow-iw)/2:(oh-ih)/2:black"
                End If

                vNode &= "[v" & i & "];"
                sbFilter.Append(vNode)
                concatArgs.Append("[v" & i & "]")
            End If

            If effectiveHasAudio OrElse isAudioOnly Then
                Dim currentAudioNode As String = If(keepList.Count > 1, "aud_sync_" & i, audioSyncNode)
                Dim aNode As String = "[" & currentAudioNode & "]atrim=start=" & startSec & ":end=" & endSec & ",asetpts=PTS-STARTPTS,aresample=async=1:first_pts=0,apad=whole_dur=" & segDuration & "[a" & i & "];"
                sbFilter.Append(aNode)
                concatArgs.Append("[a" & i & "]")
            End If
        Next

        Dim regionsCount As Integer = keepList.Count
        Dim totalExportSeconds As Double = keepList.Sum(Function(r) (r.EndTime - r.StartTime).TotalSeconds)

        If isAudioOnly Then
            Dim finalAudioMap As String
            If regionsCount > 1 Then
                sbFilter.Append(concatArgs.ToString() & "concat=n=" & regionsCount & ":v=0:a=1:unsafe=1[outa]")
                finalAudioMap = ApplyAudioEffectsToFilter(sbFilter, "[outa]", fadeIn, fadeOut, totalExportSeconds, trackVolume)
            Else
                finalAudioMap = ApplyAudioEffectsToFilter(sbFilter, "[a0]", fadeIn, fadeOut, totalExportSeconds, trackVolume)
            End If

            builder.ComplexFilter(sbFilter.ToString())
            builder.AddCustomOutputArg("-map """ & finalAudioMap & """")
            builder.DisableVideo()
            ApplyAudioCodecSettings(builder, safeFormat, compressionLevel)
            result.ActualEncoder = "Audio"
        Else
            Dim finalVideoMap As String

            If effectiveHasAudio Then
                Dim finalAudioMap As String
                If regionsCount > 1 Then
                    sbFilter.Append(concatArgs.ToString() & "concat=n=" & regionsCount & ":v=1:a=1:unsafe=1[outv][outa]")
                    finalVideoMap = ApplyVideoEffectsToFilter(sbFilter, "[outv]", videoFadeIn, videoFadeOut, totalExportSeconds)
                    finalAudioMap = ApplyAudioEffectsToFilter(sbFilter, "[outa]", fadeIn, fadeOut, totalExportSeconds, trackVolume)
                Else
                    finalVideoMap = ApplyVideoEffectsToFilter(sbFilter, "[v0]", videoFadeIn, videoFadeOut, totalExportSeconds)
                    finalAudioMap = ApplyAudioEffectsToFilter(sbFilter, "[a0]", fadeIn, fadeOut, totalExportSeconds, trackVolume)
                End If

                builder.ComplexFilter(sbFilter.ToString())
                builder.AddCustomOutputArg("-map """ & finalVideoMap & """")
                builder.AddCustomOutputArg("-map """ & finalAudioMap & """")
            Else
                If regionsCount > 1 Then
                    sbFilter.Append(concatArgs.ToString() & "concat=n=" & regionsCount & ":v=1:a=0:unsafe=1[outv]")
                    finalVideoMap = ApplyVideoEffectsToFilter(sbFilter, "[outv]", videoFadeIn, videoFadeOut, totalExportSeconds)
                Else
                    finalVideoMap = ApplyVideoEffectsToFilter(sbFilter, "[v0]", videoFadeIn, videoFadeOut, totalExportSeconds)
                End If

                builder.ComplexFilter(sbFilter.ToString())
                builder.AddCustomOutputArg("-map """ & finalVideoMap & """")
                builder.DisableAudio()
            End If

            Dim enc As String = DetermineVideoEncoder(videoEncoderLabel, safeFormat, isNvidiaGpu, isAmdGpu)

            If enc = "copy" AndAlso hasComplexFilters Then
                enc = If(isNvidiaGpu, "h264_nvenc", If(isAmdGpu, "h264_amf", "libx264"))
                result.IsFallbackApplied = True
            End If

            Dim qualityArgs As String = BuildQualityArgs(enc, compressionLevel, isNvidiaGpu, isAmdGpu)

            builder.VideoCodec(enc)
            If Not String.IsNullOrEmpty(qualityArgs) Then builder.AddCustomOutputArg(qualityArgs)

            Dim ext As String = GetOutputExtension(safeFormat).ToLowerInvariant()
            If enc = "copy" Then
                builder.AddCustomOutputArg("-avoid_negative_ts make_zero -max_muxing_queue_size 1024")
            Else
                Dim forceYuv420p As Boolean = ext <> "webm" AndAlso ext <> "gif"
                If forceYuv420p Then
                    builder.AddCustomOutputArg("-avoid_negative_ts make_zero -max_muxing_queue_size 1024 -pix_fmt yuv420p")
                Else
                    builder.AddCustomOutputArg("-avoid_negative_ts make_zero -max_muxing_queue_size 1024")
                End If
            End If

            If (ext = "mp4" OrElse ext = "mov" OrElse ext = "m4v") AndAlso (enc = "libx265" OrElse enc = "hevc_nvenc" OrElse enc = "hevc_amf") Then
                builder.AddCustomOutputArg("-tag:v hvc1")
            End If

            If effectiveHasAudio Then
                ApplyAudioCodecSettings(builder, safeFormat, compressionLevel)
            End If

            result.ActualEncoder = enc
        End If

        builder.SetOutput(outputFilePath)
        result.Arguments = builder.Build()
        Return result
    End Function

    Public Shared Function BuildPreviewCommand(videoPath As String, targetTime As TimeSpan, isAudio As Boolean) As String
        Dim builder As New FFmpegFluentBuilder()
        builder.HideBannerAndErrors().Overwrite()
        builder.AddCustomInputArg("-hwaccel none")
        builder.InputSeek(targetTime).AddInput(videoPath)

        If isAudio Then
            builder.ComplexFilter("aformat=channel_layouts=mono,showwavespic=s=1280x720:colors=0x00bfff")
        Else
            builder.VideoFilter("scale=1280:-1")
            builder.VideoQualityOrPreset("-q:v", "2")
        End If

        builder.ExtractFrames(1).VideoCodec("bmp").Format("image2pipe").SetOutput("-")
        Return builder.Build()
    End Function

    Public Shared Sub BuildContactSheetCommands(fullVideoPath As String, durationSeconds As Double, segmentStart As TimeSpan, isAudio As Boolean, isImage As Boolean, hasAudio As Boolean, targetContentWidth As Integer, contentHeight As Integer, audioHeight As Integer, tempFile1 As String, tempFile2 As String, ByRef args1 As String, ByRef args2 As String, Optional isAudioReplaced As Boolean = False, Optional externalAudioPath As String = "", Optional audioOffset As TimeSpan = Nothing)
        Dim w_even As Integer = (targetContentWidth \ 2) * 2
        Dim h1_even As Integer = (contentHeight \ 2) * 2
        Dim audio_even As Integer = (audioHeight \ 2) * 2
        Dim threadsCount As Integer = Environment.ProcessorCount

        Dim actualAudioReplaced As Boolean = isAudioReplaced AndAlso Not String.IsNullOrWhiteSpace(externalAudioPath)
        Dim effectiveHasAudio As Boolean = hasAudio OrElse actualAudioReplaced
        Dim audioInputPath As String = If(actualAudioReplaced, externalAudioPath, fullVideoPath)
        Dim actualAudioOffset As TimeSpan = If(audioOffset <> Nothing, audioOffset, TimeSpan.Zero)

        If isAudio Then
            Dim h1_safe As Integer = If(h1_even > 0, h1_even, 100)

            Dim audioSeekSec As Double = segmentStart.TotalSeconds - actualAudioOffset.TotalSeconds
            Dim delayMs As Long = 0
            If audioSeekSec < 0 Then
                delayMs = CLng(Math.Abs(audioSeekSec) * 1000)
                audioSeekSec = 0
            End If

            Dim delayFilter As String = If(delayMs > 0, "adelay=delays=" & delayMs & ":all=1,", "")
            Dim aTrim As String = "asetpts=PTS-STARTPTS," & delayFilter & "apad,atrim=start=0:end=" & durationSeconds.ToString("F3", CultureInfo.InvariantCulture) & ",asetpts=PTS-STARTPTS,"

            Dim b1 As New FFmpegFluentBuilder()
            b1.HideBannerAndErrors().Overwrite().Threads(threadsCount).InputSeek(TimeSpan.FromSeconds(audioSeekSec)).AddInput(audioInputPath).OutputDuration(durationSeconds)
            b1.ComplexFilter("[0:a]aformat=sample_rates=8000:channel_layouts=stereo," & aTrim & "pan=mono|c0=FL,showwavespic=s=" & w_even & "x" & h1_safe & ":colors=0x00bfff:scale=cbrt[out]")
            b1.Map("[out]").ExtractFrames(1)
            args1 = b1.SetOutput(tempFile1).Build()

            If w_even > 0 AndAlso audio_even > 0 Then
                Dim b2 As New FFmpegFluentBuilder()
                b2.HideBannerAndErrors().Overwrite().Threads(threadsCount).InputSeek(TimeSpan.FromSeconds(audioSeekSec)).AddInput(audioInputPath).OutputDuration(durationSeconds)
                b2.ComplexFilter("[0:a]aformat=sample_rates=8000:channel_layouts=stereo," & aTrim & "pan=mono|c0=FR,showwavespic=s=" & w_even & "x" & Math.Max(20, audio_even).ToString() & ":colors=0x00ff7f:scale=cbrt[out]")
                b2.Map("[out]").ExtractFrames(1)
                args2 = b2.SetOutput(tempFile2).Build()
            End If
        ElseIf isImage Then
            Dim b1 As New FFmpegFluentBuilder()
            b1.HideBannerAndErrors().Overwrite().Threads(threadsCount).AddInput(fullVideoPath)
            b1.VideoFilter("scale=" & w_even & ":" & h1_even & ":force_original_aspect_ratio=decrease,pad=" & w_even & ":" & h1_even & ":(ow-iw)/2:(oh-ih)/2:black")
            b1.ExtractFrames(1).VideoQualityOrPreset("-q:v", "2")
            args1 = b1.SetOutput(tempFile1).Build()
        Else
            Dim numThumbs As Integer = Math.Max(6, Math.Min(targetContentWidth \ 37, 40))
            Dim thumbHeight As Integer = (contentHeight \ 2) * 2
            Dim innerWidth As Integer = Math.Max(2, ((targetContentWidth - (numThumbs - 1) * 2) \ numThumbs \ 2) * 2)
            Dim safeDuration As Double = Math.Max(0.1, durationSeconds)

            Dim intervalStr As String = (safeDuration / (numThumbs + 0.2)).ToString("F5", CultureInfo.InvariantCulture)
            Dim vTrim As String = "setpts=PTS-STARTPTS,trim=start=0:end=" & safeDuration.ToString("F3", CultureInfo.InvariantCulture) & ",setpts=PTS-STARTPTS,"

            Dim finalWidth As Integer = numThumbs * innerWidth + (numThumbs - 1) * 2
            Dim videoFilter As String = vTrim & "fps=1/" & intervalStr & ",scale=" & innerWidth & ":" & thumbHeight & ":force_original_aspect_ratio=increase,crop=" & innerWidth & ":" & thumbHeight & ",pad=" & (innerWidth + 2).ToString() & ":" & thumbHeight & ":0:0:color=0x1a1a22,tile=" & numThumbs & "x1,crop=" & finalWidth & ":" & thumbHeight & ":0:0,pad=" & w_even & ":" & thumbHeight & ":0:0:color=0x1a1a22"

            Dim b1 As New FFmpegFluentBuilder()
            b1.HideBannerAndErrors().Overwrite().Threads(threadsCount).InputSeek(segmentStart).AddInput(fullVideoPath).OutputDuration(durationSeconds)
            b1.VideoFilter(videoFilter).ExtractFrames(1).VideoQualityOrPreset("-q:v", "2")
            args1 = b1.SetOutput(tempFile1).Build()

            If w_even > 0 AndAlso audio_even > 0 AndAlso effectiveHasAudio Then
                Dim audioSeekSec As Double = segmentStart.TotalSeconds - actualAudioOffset.TotalSeconds
                Dim delayMs As Long = 0
                If audioSeekSec < 0 Then
                    delayMs = CLng(Math.Abs(audioSeekSec) * 1000)
                    audioSeekSec = 0
                End If

                Dim delayFilter As String = If(delayMs > 0, "adelay=delays=" & delayMs & ":all=1,", "")
                Dim aTrim As String = "asetpts=PTS-STARTPTS," & delayFilter & "apad,atrim=start=0:end=" & safeDuration.ToString("F3", CultureInfo.InvariantCulture) & ",asetpts=PTS-STARTPTS,"

                Dim b2 As New FFmpegFluentBuilder()
                b2.HideBannerAndErrors().Overwrite().Threads(threadsCount).InputSeek(TimeSpan.FromSeconds(audioSeekSec)).AddInput(audioInputPath).OutputDuration(durationSeconds)
                b2.ComplexFilter("[0:a]aformat=sample_rates=8000:channel_layouts=stereo," & aTrim & "showwavespic=s=" & w_even & "x" & Math.Max(20, audio_even).ToString() & ":colors=0x00bfff|0x00ff7f:scale=cbrt:split_channels=1[out]")
                b2.Map("[out]").ExtractFrames(1)
                args2 = b2.SetOutput(tempFile2).Build()
            End If
        End If
    End Sub

    Public Shared Function BuildAudioStripRawCommand(
        videoPath As String,
        segmentStart As TimeSpan,
        durationSec As Double,
        stripWidth As Integer,
        stripHeight As Integer,
        hasAudio As Boolean,
        isAudioReplaced As Boolean,
        externalAudioPath As String,
        audioOffset As TimeSpan
    ) As String
        Dim actualAudioReplaced As Boolean = isAudioReplaced AndAlso Not String.IsNullOrWhiteSpace(externalAudioPath)
        If Not hasAudio AndAlso Not actualAudioReplaced Then Return String.Empty

        Dim w = (stripWidth \ 2) * 2
        Dim h = (stripHeight \ 2) * 2
        If w < 2 Then w = 2
        If h < 2 Then h = 2

        Dim audioInputPath As String = If(actualAudioReplaced, externalAudioPath, videoPath)

        Dim seekSec As Double = segmentStart.TotalSeconds - audioOffset.TotalSeconds
        Dim delayMs As Long = 0
        If seekSec < 0 Then
            delayMs = CLng(Math.Abs(seekSec) * 1000)
            seekSec = 0
        End If

        Dim delayFilter As String = If(delayMs > 0, "adelay=delays=" & delayMs & ":all=1,", "")

        Dim filterComplex As String =
        "aformat=sample_rates=8000:channel_layouts=stereo," &
        "asetpts=PTS-STARTPTS," &
        delayFilter &
        "apad," &
        "atrim=start=0:end=" & durationSec.ToString("F3", CultureInfo.InvariantCulture) & "," &
        "asetpts=PTS-STARTPTS," &
        "showwavespic=s=" & w & "x" & h & ":colors=0x00bfff|0x00ff7f:scale=cbrt:split_channels=1[out]"

        Dim builder As New FFmpegFluentBuilder()
        builder.HideBannerAndErrors() _
           .Overwrite() _
           .Threads(Environment.ProcessorCount) _
           .InputSeek(TimeSpan.FromSeconds(seekSec)) _
           .AddInput(audioInputPath) _
           .OutputDuration(durationSec) _
           .DisableVideo() _
           .ComplexFilter(filterComplex) _
           .Map("[out]") _
           .ExtractFrames(1) _
           .Format("rawvideo") _
           .AddCustomOutputArg("-pix_fmt bgra") _
           .SetOutput("-")

        Return builder.Build()
    End Function

    Public Shared Function BuildPrecisionExportCommand(
        inputFilePath As String,
        outputFilePath As String,
        keepRegions As List(Of KeepRegion),
        hasAudio As Boolean,
        isCropActive As Boolean,
        cropX As Integer,
        cropY As Integer,
        cropW As Integer,
        cropH As Integer,
        audioOffset As TimeSpan,
        isNvidiaGpu As Boolean,
        Optional isAudioReplaced As Boolean = False,
        Optional externalAudioPath As String = "",
        Optional fadeIn As TimeSpan = Nothing,
        Optional fadeOut As TimeSpan = Nothing,
        Optional trackVolume As Single = 1.0F
    ) As BuildResult

        Dim builder As New FFmpegFluentBuilder()
        builder.HideBannerAndErrors().Overwrite().AddStats()

        builder.GeneratePts().AddInput(inputFilePath)

        Dim actualAudioReplaced As Boolean = isAudioReplaced AndAlso Not String.IsNullOrEmpty(externalAudioPath)
        If actualAudioReplaced Then
            builder.AddInput(externalAudioPath)
        End If

        Dim sbFilter As New StringBuilder()
        Dim concatArgs As New StringBuilder()

        Dim audioSourceNode As String = If(actualAudioReplaced, "1:a:0", "0:a:0")
        If hasAudio OrElse actualAudioReplaced Then
            If audioOffset.TotalMilliseconds > 0 Then
                Dim delayMs As Integer = CInt(Math.Round(audioOffset.TotalMilliseconds))
                sbFilter.Append("[" & audioSourceNode & "]adelay=delays=" & delayMs & ":all=1[aud_sync];")
                audioSourceNode = "aud_sync"

            ElseIf audioOffset.TotalMilliseconds < 0 Then
                Dim advanceSec As String = Math.Abs(audioOffset.TotalSeconds).ToString("0.000", CultureInfo.InvariantCulture)
                sbFilter.Append("[" & audioSourceNode & "]atrim=start=" & advanceSec & ",asetpts=PTS-STARTPTS[aud_sync];")
                audioSourceNode = "aud_sync"
            End If
        End If

        If (hasAudio OrElse actualAudioReplaced) AndAlso keepRegions.Count > 1 Then
            Dim splitNode As String = String.Join("", Enumerable.Range(0, keepRegions.Count).Select(Function(i) "[aud_sync_" & i & "]"))
            sbFilter.Append("[" & audioSourceNode & "]asplit=" & keepRegions.Count & splitNode & ";")
        End If

        For i As Integer = 0 To keepRegions.Count - 1
            Dim region = keepRegions(i)

            Dim exactStart As Double = region.StartTime.TotalSeconds
            Dim exactEnd As Double = region.EndTime.TotalSeconds
            Dim exactDur As Double = exactEnd - exactStart

            Dim startSec As String = exactStart.ToString("0.000000", CultureInfo.InvariantCulture)
            Dim endSec As String = exactEnd.ToString("0.000000", CultureInfo.InvariantCulture)
            Dim segDuration As String = exactDur.ToString("0.000000", CultureInfo.InvariantCulture)

            Dim vFilter As String = "[0:v:0]trim=start=" & startSec & ":end=" & endSec & ",setpts=PTS-STARTPTS"

            If isCropActive AndAlso cropW > 0 AndAlso cropH > 0 Then
                vFilter &= ",crop=" & cropW & ":" & cropH & ":" & cropX & ":" & cropY
            End If

            vFilter &= "[v" & i & "];"
            sbFilter.Append(vFilter)
            concatArgs.Append("[v" & i & "]")

            If hasAudio OrElse actualAudioReplaced Then
                Dim currentAudioNode As String = If(keepRegions.Count > 1, "aud_sync_" & i, audioSourceNode)
                Dim aFilter As String = "[" & currentAudioNode & "]atrim=start=" & startSec & ":end=" & endSec & ",asetpts=PTS-STARTPTS,aresample=async=1,apad=whole_dur=" & segDuration & "[a" & i & "];"
                sbFilter.Append(aFilter)
                concatArgs.Append("[a" & i & "]")
            End If
        Next

        Dim regionsCount As Integer = keepRegions.Count
        Dim totalExportSeconds As Double = keepRegions.Sum(Function(r) (r.EndTime - r.StartTime).TotalSeconds)

        If hasAudio OrElse actualAudioReplaced Then
            sbFilter.Append(concatArgs.ToString() & "concat=n=" & regionsCount & ":v=1:a=1[outv][outa]")

            Dim finalAudioMap As String = ApplyAudioEffectsToFilter(sbFilter, "[outa]", fadeIn, fadeOut, totalExportSeconds, trackVolume)

            builder.ComplexFilter(sbFilter.ToString())
            builder.AddCustomOutputArg("-map ""[outv]""")
            builder.AddCustomOutputArg("-map """ & finalAudioMap & """")
        Else
            sbFilter.Append(concatArgs.ToString() & "concat=n=" & regionsCount & ":v=1:a=0[outv]")
            builder.ComplexFilter(sbFilter.ToString())
            builder.AddCustomOutputArg("-map ""[outv]""")
        End If

        Dim actualEncoder As String
        Dim isWebm As Boolean = outputFilePath.EndsWith(".webm", StringComparison.OrdinalIgnoreCase)

        If isWebm Then
            actualEncoder = "libvpx-vp9"
            ' ИСПРАВЛЕНИЕ: Консистентный вызов для VP9 без альфы и без -b:v 0
            builder.AddCustomOutputArg("-c:v libvpx-vp9 -crf 26 -b:v 10M -row-mt 1 -auto-alt-ref 0 -pix_fmt yuv420p")

            If hasAudio OrElse actualAudioReplaced Then
                builder.AddCustomOutputArg("-c:a libopus -b:a 128k")
            End If
        Else
            If isNvidiaGpu Then
                actualEncoder = "h264_nvenc"
                builder.AddCustomOutputArg("-c:v h264_nvenc -preset p4 -tune hq -rc vbr -cq 24 -b:v 0")
            Else
                actualEncoder = "libx264"
                builder.AddCustomOutputArg("-c:v libx264 -preset veryfast -crf 18")
            End If

            If hasAudio OrElse actualAudioReplaced Then
                builder.AddCustomOutputArg("-c:a aac -b:a 192k")
            End If
        End If

        If Not isWebm Then
            builder.AddCustomOutputArg("-avoid_negative_ts make_zero -movflags +faststart")
        Else
            builder.AddCustomOutputArg("-avoid_negative_ts make_zero")
        End If

        builder.SetOutput(outputFilePath)

        Dim result As New BuildResult With {
            .Arguments = builder.Build(),
            .ActualEncoder = actualEncoder,
            .IsFallbackApplied = Not isNvidiaGpu AndAlso Not isWebm
        }

        Return result
    End Function

End Class