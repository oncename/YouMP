Option Strict On
Option Explicit On

Imports System.Collections.Generic
Imports System.Globalization
Imports System.Linq
Imports System.Text
Imports yoump.IServices

Public Class NleExportGraphBuilder

    ' Вспомогательный метод определения параметров качества, перенесенный из старого билдера
    Private Shared Function BuildQualityArgs(enc As String, compLevel As String, isNvidiaGpu As Boolean, isAmdGpu As Boolean) As String
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

        Else
            ' Программные кодеки (libx264, libx265)
            Dim presetVal As String = If(compLevel = "Minimal", "slow", If(compLevel = "Low", "medium", If(compLevel = "High", "fast", If(compLevel = "Maximum", "veryfast", "medium"))))
            Dim crfVal As String = If(compLevel = "Minimal", "18", If(compLevel = "Low", "23", If(compLevel = "Medium", "28", If(compLevel = "High", "33", "38"))))
            Return $"-preset {presetVal} -crf {crfVal} -maxrate {maxRate} -bufsize {bufSize}"
        End If
    End Function

    ' Расширенная сигнатура с параметрами качества
    Public Shared Function BuildExportArguments(model As ProjectModel, outputFilePath As String, targetW As Integer, targetH As Integer, fps As Double, encoder As String, compressionLevel As String, isNvidiaGpu As Boolean, isAmdGpu As Boolean) As String
        Dim builder As New FFmpegFluentBuilder()
        builder.HideBannerAndErrors().Overwrite().AddStats()

        Dim uniqueFiles As New Dictionary(Of String, Integer)(StringComparer.OrdinalIgnoreCase)
        Dim inputIndex As Integer = 0

        Dim allClips = model.Tracks.Where(Function(t) Not t.IsMuted).SelectMany(Function(t) t.Clips).OrderBy(Function(c) c.TimelineStart).ToList()

        For Each clip In allClips
            If Not uniqueFiles.ContainsKey(clip.FilePath) Then
                uniqueFiles.Add(clip.FilePath, inputIndex)
                builder.AddInput(clip.FilePath)
                inputIndex += 1
            End If
        Next

        If uniqueFiles.Count = 0 Then Return String.Empty

        Dim filterGraph As New StringBuilder()
        Dim durationSec As Double = (model.MarkerEnd - model.MarkerStart).TotalSeconds
        Dim durationStr As String = durationSec.ToString("0.000", CultureInfo.InvariantCulture)
        Dim fpsStr As String = fps.ToString("0.00", CultureInfo.InvariantCulture)

        filterGraph.Append($"color=c=black:s={targetW}x{targetH}:d={durationStr}:r={fpsStr}[bg0];")

        Dim videoOverlayChains As New List(Of String)()
        Dim audioMixNodes As New List(Of String)()

        Dim vNodeCounter As Integer = 0
        Dim aNodeCounter As Integer = 0

        For Each track In model.Tracks
            If track.IsMuted Then Continue For

            For Each clip In track.Clips
                If clip.TimelineEnd <= model.MarkerStart OrElse clip.TimelineStart >= model.MarkerEnd Then Continue For

                Dim fileIdx = uniqueFiles(clip.FilePath)
                Dim globalStart = Math.Max(0, (clip.TimelineStart - model.MarkerStart).TotalSeconds)
                Dim globalEnd = Math.Min(durationSec, (clip.TimelineEnd - model.MarkerStart).TotalSeconds)
                Dim localStart = clip.SourceIn.TotalSeconds + If(clip.TimelineStart < model.MarkerStart, (model.MarkerStart - clip.TimelineStart).TotalSeconds, 0)

                Dim startStr = localStart.ToString("0.000", CultureInfo.InvariantCulture)
                Dim endStr = (localStart + (globalEnd - globalStart)).ToString("0.000", CultureInfo.InvariantCulture)
                Dim enableStr = $"between(t,{globalStart.ToString("0.000", CultureInfo.InvariantCulture)},{globalEnd.ToString("0.000", CultureInfo.InvariantCulture)})"

                If track.Type = TargetFormatType.Video OrElse track.Type = TargetFormatType.Image Then
                    Dim vNode = $"[v_node_{vNodeCounter}]"
                    Dim vChain As New List(Of String)()

                    If track.Type = TargetFormatType.Video Then
                        vChain.Add($"trim=start={startStr}:end={endStr}")
                        vChain.Add("setpts=PTS-STARTPTS")
                    End If

                    If clip.Scale <> 1.0F Then
                        Dim sc = clip.Scale.ToString("0.00", CultureInfo.InvariantCulture)
                        vChain.Add($"scale=iw*{sc}:ih*{sc}")
                    End If
                    If clip.Rotation <> 0.0F Then
                        Dim rad = (clip.Rotation * Math.PI / 180.0).ToString("0.000", CultureInfo.InvariantCulture)
                        vChain.Add($"rotate={rad}:c=none:ow=rotw({rad}):oh=roth({rad})")
                    End If

                    If clip.FadeIn > TimeSpan.Zero Then
                        vChain.Add($"fade=t=in:st=0:d={clip.FadeIn.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture)}:alpha=1")
                    End If

                    vChain.Add("format=rgba")
                    If clip.Volume < 1.0F Then
                        Dim alpha = clip.Volume.ToString("0.00", CultureInfo.InvariantCulture)
                        vChain.Add($"colorchannelmixer=aa={alpha}")
                    End If

                    filterGraph.Append($"[{fileIdx}:v]{String.Join(",", vChain)}{vNode};")

                    Dim cx = (targetW / 2.0F) + clip.PositionX
                    Dim cy = (targetH / 2.0F) + clip.PositionY
                    Dim ffX = $"({cx.ToString("0.0", CultureInfo.InvariantCulture)}-(w/2))"
                    Dim ffY = $"({cy.ToString("0.0", CultureInfo.InvariantCulture)}-(h/2))"

                    videoOverlayChains.Add($"{vNode}overlay=x={ffX}:y={ffY}:enable='{enableStr}'")
                    vNodeCounter += 1
                End If

                If track.Type = TargetFormatType.Audio OrElse track.Type = TargetFormatType.Video Then
                    Dim aNode = $"[a_node_{aNodeCounter}]"
                    Dim aChain As New List(Of String) From {
                        $"atrim=start={startStr}:end={endStr}",
                        "asetpts=PTS-STARTPTS"
                    }

                    Dim delayMs = CInt(globalStart * 1000)
                    If delayMs > 0 Then
                        aChain.Add($"adelay={delayMs}|{delayMs}")
                    End If

                    If clip.Volume <> 1.0F Then
                        aChain.Add($"volume={clip.Volume.ToString("0.00", CultureInfo.InvariantCulture)}")
                    End If
                    If clip.FadeIn > TimeSpan.Zero Then
                        aChain.Add($"afade=t=in:st={globalStart.ToString("0.000", CultureInfo.InvariantCulture)}:d={clip.FadeIn.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture)}")
                    End If

                    filterGraph.Append($"[{fileIdx}:a]{String.Join(",", aChain)}{aNode};")
                    audioMixNodes.Add(aNode)
                    aNodeCounter += 1
                End If
            Next
        Next

        Dim lastBg = "[bg0]"
        For i As Integer = 0 To videoOverlayChains.Count - 1
            Dim nextBg = $"[bg{i + 1}]"
            filterGraph.Append($"{lastBg}{videoOverlayChains(i)}{nextBg};")
            lastBg = nextBg
        Next

        Dim finalAudioNode = ""
        If audioMixNodes.Count > 0 Then
            finalAudioNode = "[outa]"
            filterGraph.Append($"{String.Join("", audioMixNodes)}amix=inputs={audioMixNodes.Count}:duration=first:dropout_transition=0{finalAudioNode}")
        End If

        Dim finalFilter = filterGraph.ToString()
        If finalFilter.EndsWith(";"c) Then finalFilter = finalFilter.Substring(0, finalFilter.Length - 1)

        builder.ComplexFilter(finalFilter)

        If videoOverlayChains.Count > 0 Then
            builder.AddCustomOutputArg($"-map ""{lastBg}""")
        Else
            builder.DisableVideo()
        End If

        If audioMixNodes.Count > 0 Then
            builder.AddCustomOutputArg($"-map ""{finalAudioNode}""")
        Else
            builder.DisableAudio()
        End If

        ' === Применяем параметры качества ===
        Dim qualityArgs As String = BuildQualityArgs(encoder, compressionLevel, isNvidiaGpu, isAmdGpu)
        builder.VideoCodec(encoder).AddCustomOutputArg("-pix_fmt yuv420p")

        If Not String.IsNullOrEmpty(qualityArgs) Then
            builder.AddCustomOutputArg(qualityArgs)
        End If

        If audioMixNodes.Count > 0 Then builder.AudioCodec("aac").AudioBitrate("192k")

        builder.SetOutput(outputFilePath)
        Return builder.Build()
    End Function
End Class