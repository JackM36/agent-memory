$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$installer = Join-Path $scriptRoot "tools\install-interactive.js"

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
	$dialog.Description = "Select the target project folder"
	$dialog.ShowNewFolderButton = $true
	$result = $dialog.ShowDialog()
	if ($result -ne [System.Windows.Forms.DialogResult]::OK) {
		throw "Install cancelled."
	}

	return $dialog.SelectedPath
}

function Select-Integrations {
	Add-Type -AssemblyName System.Windows.Forms
	Add-Type -AssemblyName System.Drawing

	$form = New-Object System.Windows.Forms.Form
	$form.Text = "Agent Memory Integrations"
	$form.Width = 360
	$form.Height = 280
	$form.StartPosition = "CenterScreen"

	$label = New-Object System.Windows.Forms.Label
	$label.Text = "Choose native integrations to install:"
	$label.AutoSize = $true
	$label.Left = 12
	$label.Top = 12
	$form.Controls.Add($label)

	$list = New-Object System.Windows.Forms.CheckedListBox
	$list.Left = 12
	$list.Top = 40
	$list.Width = 310
	$list.Height = 120
	[void]$list.Items.Add("claude")
	[void]$list.Items.Add("codex")
	[void]$list.Items.Add("gemini")
	[void]$list.Items.Add("opencode")
	$form.Controls.Add($list)

	$ok = New-Object System.Windows.Forms.Button
	$ok.Text = "Install"
	$ok.Left = 166
	$ok.Top = 185
	$ok.Width = 75
	$ok.DialogResult = [System.Windows.Forms.DialogResult]::OK
	$form.AcceptButton = $ok
	$form.Controls.Add($ok)

	$cancel = New-Object System.Windows.Forms.Button
	$cancel.Text = "Cancel"
	$cancel.Left = 247
	$cancel.Top = 185
	$cancel.Width = 75
	$cancel.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
	$form.CancelButton = $cancel
	$form.Controls.Add($cancel)

	$result = $form.ShowDialog()
	if ($result -ne [System.Windows.Forms.DialogResult]::OK) {
		throw "Install cancelled."
	}

	$selected = @()
	foreach ($item in $list.CheckedItems) {
		$selected += [string]$item
	}

	if ($selected.Count -eq 0) {
		return "none"
	}

	return ($selected -join ",")
}

function Select-ClaudeMode {
	Add-Type -AssemblyName System.Windows.Forms
	Add-Type -AssemblyName System.Drawing

	$form = New-Object System.Windows.Forms.Form
	$form.Text = "CLAUDE.md Setup"
	$form.Width = 390
	$form.Height = 190
	$form.StartPosition = "CenterScreen"

	$label = New-Object System.Windows.Forms.Label
	$label.Text = "Choose CLAUDE.md behavior:"
	$label.AutoSize = $true
	$label.Left = 12
	$label.Top = 12
	$form.Controls.Add($label)

	$combo = New-Object System.Windows.Forms.ComboBox
	$combo.Left = 12
	$combo.Top = 40
	$combo.Width = 340
	$combo.DropDownStyle = "DropDownList"
	[void]$combo.Items.Add("reference")
	[void]$combo.Items.Add("full")
	[void]$combo.Items.Add("none")
	$combo.SelectedIndex = 0
	$form.Controls.Add($combo)

	$ok = New-Object System.Windows.Forms.Button
	$ok.Text = "OK"
	$ok.Left = 196
	$ok.Top = 92
	$ok.Width = 75
	$ok.DialogResult = [System.Windows.Forms.DialogResult]::OK
	$form.AcceptButton = $ok
	$form.Controls.Add($ok)

	$cancel = New-Object System.Windows.Forms.Button
	$cancel.Text = "Cancel"
	$cancel.Left = 277
	$cancel.Top = 92
	$cancel.Width = 75
	$cancel.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
	$form.CancelButton = $cancel
	$form.Controls.Add($cancel)

	$result = $form.ShowDialog()
	if ($result -ne [System.Windows.Forms.DialogResult]::OK) {
		throw "Install cancelled."
	}

	return [string]$combo.SelectedItem
}

function Select-Hooks {
	Add-Type -AssemblyName System.Windows.Forms
	$result = [System.Windows.Forms.MessageBox]::Show(
		"Install the Stop-hook enforcement for Claude/Codex? It nudges the agent to write a report when a session changes code but records none.",
		"Agent Memory Hooks",
		[System.Windows.Forms.MessageBoxButtons]::YesNo,
		[System.Windows.Forms.MessageBoxIcon]::Question)
	return ($result -eq [System.Windows.Forms.DialogResult]::Yes)
}

try {
	Test-Node
	$target = Select-TargetFolder
	$agents = Select-Integrations
	$claude = Select-ClaudeMode

	$hooksArg = @()
	$agentList = $agents.Split(",")
	if (($agentList -contains "claude") -or ($agentList -contains "codex")) {
		if (Select-Hooks) { $hooksArg = @("--hooks") } else { $hooksArg = @("--no-hooks") }
	}

	& node $installer --target $target --agents $agents --claude $claude @hooksArg
	if ($LASTEXITCODE -ne 0) {
		exit $LASTEXITCODE
	}
}
catch {
	Write-Error $_.Exception.Message
	exit 1
}
