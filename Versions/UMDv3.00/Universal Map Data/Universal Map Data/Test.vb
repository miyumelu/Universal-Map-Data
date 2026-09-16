Imports System.IO

Public NotInheritable Class Test

    Public Shared Function Run(UMDPath As String) As String
        Dim sb As New System.Text.StringBuilder()
        sb.AppendLine("=== UMD Map Test ===")
        sb.AppendLine($"File : {UMDPath}")

        If Not File.Exists(UMDPath) Then
            sb.AppendLine("ERROR: File not found!")
            Return sb.ToString
        End If

        Dim fi As New FileInfo(UMDPath)
        sb.AppendLine($"Size : {fi.Length / 1024.0 / 1024.0:F1} MB")

        GC.Collect()
        GC.WaitForPendingFinalizers()
        GC.Collect()
        Dim memBefore = GC.GetTotalMemory(True)

        Dim sw = System.Diagnostics.Stopwatch.StartNew()
        Dim _reader = Reader.TryCreate(UMDPath)
        sw.Stop()

        Dim memAfter = GC.GetTotalMemory(False)
        sb.AppendLine($"Load : {sw.ElapsedMilliseconds} ms, RAM-gain ~{(memAfter - memBefore) / 1024.0 / 1024.0:F1} MB")

        If _reader Is Nothing OrElse Not _reader.IsAvailable Then
            sb.AppendLine("ERROR: Reader could not read the file (Magic/Data corrupted).")
            Return sb.ToString
        End If

        sb.AppendLine($"ZoomMin : {_reader.ExportMinZoom}")
        sb.AppendLine($"ZoomMax : {_reader.ExportMaxZoom}")
        sb.AppendLine($"Tiles   : {_reader.ExportMinZoom}..{_reader.ExportMaxZoom} Zoom-levels available")

        sb.AppendLine("")
        sb.AppendLine("--- Overlay compatible ---")
        If _reader.HasOverlays Then
            sb.AppendLine($"Overlays : OK - {_reader.OverlayCount} Entries, {_reader.OverlayIconCount} Icons embedded")
        Else
            sb.AppendLine("Overlays : NOT AVAILABLE - UMD has no Overlays.json/Overlays-folder packed")
            sb.AppendLine("           (or created with a packer without Overlay-Support).")
        End If

        Dim stops = _reader.BuildOfflineRenderZoomStops()
        sb.AppendLine($"ZoomStops: {stops.Length} Levels ({If(stops.Length > 0, $"{stops(0):F2} – {stops(stops.Length - 1):F2}", "–")})")

        sb.AppendLine("")
        sb.AppendLine("Test-Render (512x512, Centerpoint)…")
        sw.Restart()
        Dim usedZ As Integer = -1
        Dim bmp = _reader.Render(0, 0, 1.0F, 512, 512, usedZ)
        sw.Stop()

        If bmp Is Nothing Then
            sb.AppendLine("WARNING: Render gave Nothing back (Position 0/0 possibly outside the map).")
            sb.AppendLine("         Trying with actual truck position.")
        Else
            sb.AppendLine($"OK  – {bmp.Width}x{bmp.Height} px, TileZoom={usedZ}, {sw.ElapsedMilliseconds} ms")

            Dim outPath = Path.Combine(Path.GetDirectoryName(UMDPath), "UMD_Test_Render.png")
            Try
                bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png)
                sb.AppendLine($"Image saved: {outPath}")
            Catch ex As Exception
                sb.AppendLine($"Image could not be saved: {ex.Message}")
            End Try
        End If

        If stops.Length > 0 Then
            Dim sampleIdx = {0, stops.Length \ 2, stops.Length - 1}.Distinct()
            For Each idx In sampleIdx
                Dim rz = stops(idx)
                sw.Restart()
                Dim bmp2 = _reader.Render(0, 0, rz, 512, 512, usedZ)
                sw.Stop()
                sb.AppendLine($"Zoom {rz:F3} (Idx {idx}) – TileZoom={usedZ}, {If(bmp2 IsNot Nothing, "OK", "EMPTY")}, {sw.ElapsedMilliseconds} ms")
            Next
        End If

        _reader.Dispose()
        sb.AppendLine("")
        sb.AppendLine("=== Test completed ===")
        Return sb.ToString
    End Function

End Class