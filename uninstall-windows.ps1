$ErrorActionPreference = "Stop"

function Test-Node {
	$node = Get-Command node -ErrorAction SilentlyContinue
	if ($null -eq $node) {
		throw "Node.js is required but was not found on PATH. Install Node.js, then run this script again."
	}
}

function Select-TargetFolder {
	Add-Type -AssemblyName System.Windows.Forms
	[System.Windows.Forms.Application]::EnableVisualStyles()
	$dialog = New-Object System.Windows.Forms.FolderBrowserDialog
	$dialog.Description = "Select the project folder to uninstall Agent Memory from"
	$dialog.ShowNewFolderButton = $false
	$result = $dialog.ShowDialog()
	if ($result -ne [System.Windows.Forms.DialogResult]::OK) {
		throw "Uninstall cancelled."
	}

	return $dialog.SelectedPath
}

function Select-UninstallMode {
	Add-Type -AssemblyName System.Windows.Forms
	Add-Type -AssemblyName System.Drawing

	$form = New-Object System.Windows.Forms.Form
	$form.Text = "Uninstall Agent Memory"
	$form.Width = 520
	$form.Height = 210
	$form.StartPosition = "CenterScreen"

	$label = New-Object System.Windows.Forms.Label
	$label.Text = "Choose uninstall mode:"
	$label.AutoSize = $true
	$label.Left = 12
	$label.Top = 12
	$form.Controls.Add($label)

	$combo = New-Object System.Windows.Forms.ComboBox
	$combo.Left = 12
	$combo.Top = 40
	$combo.Width = 470
	$combo.DropDownStyle = "DropDownList"
	[void]$combo.Items.Add("keep-data")
	[void]$combo.Items.Add("delete-all")
	$combo.SelectedIndex = 0
	$form.Controls.Add($combo)

	$ok = New-Object System.Windows.Forms.Button
	$ok.Text = "Uninstall"
	$ok.Left = 326
	$ok.Top = 95
	$ok.Width = 75
	$ok.DialogResult = [System.Windows.Forms.DialogResult]::OK
	$form.AcceptButton = $ok
	$form.Controls.Add($ok)

	$cancel = New-Object System.Windows.Forms.Button
	$cancel.Text = "Cancel"
	$cancel.Left = 407
	$cancel.Top = 95
	$cancel.Width = 75
	$cancel.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
	$form.CancelButton = $cancel
	$form.Controls.Add($cancel)

	$result = $form.ShowDialog()
	if ($result -ne [System.Windows.Forms.DialogResult]::OK) {
		throw "Uninstall cancelled."
	}

	$mode = [string]$combo.SelectedItem
	if ($mode -eq "delete-all") {
		$confirm = [System.Windows.Forms.MessageBox]::Show("Delete all agent memory data? This removes rules, memories, reports, uncertainties, notes, and archive.", "Confirm Delete All", [System.Windows.Forms.MessageBoxButtons]::YesNo, [System.Windows.Forms.MessageBoxIcon]::Warning)
		if ($confirm -ne [System.Windows.Forms.DialogResult]::Yes) {
			throw "Uninstall cancelled."
		}
	}

	return $mode
}

try {
	Test-Node
	$target = Select-TargetFolder
	$mode = Select-UninstallMode
	$uninstaller = Join-Path $target ".agent-memory\tools\uninstall-project-memory.js"
	if (!(Test-Path -LiteralPath $uninstaller)) {
		throw "Missing project uninstaller: $uninstaller"
	}

	& node $uninstaller --mode $mode
	if ($LASTEXITCODE -ne 0) {
		exit $LASTEXITCODE
	}
}
catch {
	Write-Error $_.Exception.Message
	exit 1
}
