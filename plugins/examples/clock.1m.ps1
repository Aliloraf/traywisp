# <traywisp.title>Clock</traywisp.title>
# <traywisp.version>1.0</traywisp.version>
# <traywisp.author>TrayWisp contributors</traywisp.author>
# <traywisp.desc>Shows the local time and date. Example plugin; TrayWisp cannot run plugins yet.</traywisp.desc>
# <traywisp.dependencies>PowerShell</traywisp.dependencies>
#
# The file name sets a one-minute refresh interval: clock.1m.ps1.
# Output follows the SwiftBar/xbar text style: the first line is the title,
# lines after "---" are menu items.

$now = Get-Date
Write-Output $now.ToString('HH:mm')
Write-Output '---'
Write-Output $now.ToString('dddd d MMMM yyyy')
Write-Output ('Week ' + [System.Globalization.ISOWeek]::GetWeekOfYear($now))
