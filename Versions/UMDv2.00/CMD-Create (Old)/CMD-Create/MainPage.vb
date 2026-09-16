Imports System.IO
Imports System.Security.Authentication.ExtendedProtection
Imports Universal_Map_Data

Public Class MainPage

    Private _window As neXt_Window_Managment_System.Window ' Variable to hold the instance of XWMS

    Private Sub MainPage_Load(sender As Object, e As EventArgs) Handles MyBase.Load
        ' XWMS Initialization
        _window = New neXt_Window_Managment_System.Window(Me) ' New instance of XWMS
        _window.AddControl(Window_Panel) ' Adding Panel as trigger for dragging the window
        _window.AddControl(Form_Label) ' Adding Label as trigger for dragging the window
        _window.SetXenDeskMode(False) ' Setting it for normal shell
        _window.SetToolWindowMode(True) ' Setting it to tool window mode (deactivating dock and resize)
        _window.ApplyRoundedCorners(20) ' Applying rounded corners with a radius of 20
    End Sub

    Private Sub Minimize_Button_Click(sender As Object, e As EventArgs) Handles Minimize_Button.Click
        Me.WindowState = FormWindowState.Minimized
    End Sub

    Private Sub Close_Button_Click(sender As Object, e As EventArgs) Handles Close_Button.Click
        Me.Close()
    End Sub

    Private Async Sub btnPack_Click(sender As Object, e As EventArgs) Handles btnPack.Click
        btnPack.Enabled = False
        progress.Value = 0
        lblStatus.ForeColor = Color.White
        lblStatus.Text = "Starting..."

        Dim src = txtSource.Text
        Dim dest = txtDest.Text

        Try
            Await Task.Run(Sub()
                               Packer.Pack(src, dest, Sub(msg)
                                                          If msg.Contains("%") Then
                                                              Dim pct = 0
                                                              If Integer.TryParse(msg.Split("%"c)(0).Trim, pct) Then
                                                                  Invoke(Sub()
                                                                             progress.Value = Math.Min(100, pct)
                                                                             lblStatus.Text = msg
                                                                         End Sub)
                                                              Else
                                                                  Invoke(Sub() lblStatus.Text = msg)

                                                              End If
                                                          End If
                                                      End Sub)
                           End Sub)

            progress.Value = 100
            lblStatus.ForeColor = Color.Lime
            lblStatus.Text = "Packing completed successfully - " & dest
        Catch ex As Exception
            lblStatus.ForeColor = Color.Orange
            lblStatus.Text = "Error: " & ex.Message
        Finally
            btnPack.Enabled = True
        End Try
    End Sub

    Private Sub btnSrc_Click(sender As Object, e As EventArgs) Handles btnSrc.Click
        Using diag As New FolderBrowserDialog
            diag.SelectedPath = txtSource.Text
            If diag.ShowDialog() = DialogResult.OK Then
                txtSource.Text = diag.SelectedPath
            End If
        End Using
    End Sub

    Private Sub btnDest_Click(sender As Object, e As EventArgs) Handles btnDest.Click
        Using diag As New SaveFileDialog() With {
            .Filter = "Universal Map Data (*.umd)|*.umd",
            .FileName = "[MAP_NAME].UMD"
        }
            If diag.ShowDialog() = DialogResult.OK Then
                txtDest.Text = diag.FileName
            End If
        End Using
    End Sub

    Private Sub btnTest_Click(sender As Object, e As EventArgs) Handles btnTest.Click
        Using diag As New OpenFileDialog() With {
            .Filter = "Universal Map Data (*.umd)|*.umd"
        }
            If diag.ShowDialog() = DialogResult.OK Then
                Dim res As String = Test.Run(diag.FileName)
                MessageBox.Show(res, "Test Results - UMD Map", MessageBoxButtons.OK, MessageBoxIcon.Information)
            End If
        End Using
    End Sub

    Private Async Sub btnMigrateCmdToUmd_Click(sender As Object, e As EventArgs) Handles btnMigrateCmdToUmd.Click
        Dim oldPath As String
        Using ofd As New OpenFileDialog With {
        .Title = "Select CMD-File",
        .Filter = "CMD-Files (*.CMD)|*.CMD|All Files (*.*)|*.*",
        .CheckFileExists = True
    }
            If ofd.ShowDialog() <> DialogResult.OK Then Return
            oldPath = ofd.FileName
        End Using

        Dim suggestedName = Path.GetFileNameWithoutExtension(oldPath) & ".UMD"
        Dim newPath As String
        Using sfd As New SaveFileDialog With {
        .Title = "Select Target for UMD-File",
        .Filter = "UMD-Files (*.UMD)|*.UMD|All Files (*.*)|*.*",
        .FileName = suggestedName,
        .InitialDirectory = Path.GetDirectoryName(oldPath)
    }
            If sfd.ShowDialog() <> DialogResult.OK Then Return
            newPath = sfd.FileName
        End Using

        If String.Equals(Path.GetFullPath(oldPath), Path.GetFullPath(newPath), StringComparison.OrdinalIgnoreCase) Then
            MessageBox.Show("Source and target path must not be identical.", "Error",
                         MessageBoxButtons.OK, MessageBoxIcon.Warning)
            Return
        End If

        btnMigrateCmdToUmd.Enabled = False
        progress.Value = 0
        lblStatus.ForeColor = Color.White
        lblStatus.Text = "Migration running …"

        Try
            Await Task.Run(Sub()
                               Migrator.Migrate(oldPath, newPath,
                                   progress:=Sub(msg)
                                                 Invoke(Sub() lblStatus.Text = msg)
                                             End Sub,
                                   progressPercent:=Sub(pct)
                                                        Invoke(Sub() progress.Value = Math.Min(100, Math.Max(0, pct)))
                                                    End Sub)
                           End Sub)

            Using r = Reader.TryLoadForVerification(newPath)
                If r Is Nothing OrElse Not r.IsAvailable Then
                    Throw New InvalidDataException("New file could not be validated.")
                End If
            End Using

            progress.Value = 100
            lblStatus.ForeColor = Color.Lime
            lblStatus.Text = "Finished."
            MessageBox.Show($"Migration successful:{Environment.NewLine}{newPath}",
                         "Success", MessageBoxButtons.OK, MessageBoxIcon.Information)

        Catch ex As Exception
            lblStatus.ForeColor = Color.Orange
            lblStatus.Text = "Error: " & ex.Message
            MessageBox.Show($"Migration failed:{Environment.NewLine}{ex.Message}",
                         "Error", MessageBoxButtons.OK, MessageBoxIcon.Error)
        Finally
            btnMigrateCmdToUmd.Enabled = True
        End Try

    End Sub
End Class

' https://miyumelu.com