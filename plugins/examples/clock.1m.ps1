# <traywisp.title>Clock</traywisp.title>
# <traywisp.version>1.0</traywisp.version>
# <traywisp.author>TrayWisp contributors</traywisp.author>
# <traywisp.desc>Shows the local time and date. Example plugin.</traywisp.desc>
# <traywisp.dependencies>PowerShell</traywisp.dependencies>
#
# The file name sets a one-minute refresh interval: clock.1m.ps1.
# Output follows the SwiftBar/xbar text style: the first line is the title,
# lines after "---" are menu items.
#
# Works with Windows PowerShell 5.1 and PowerShell 7. Day and month names follow
# the user's culture and may be non-ASCII, so the output is UTF-8.

[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)

$now = Get-Date

# ISO 8601 week: the week (Monday to Sunday) belongs to the year of its Thursday.
# Written out because [System.Globalization.ISOWeek] is not in Windows PowerShell 5.1.
$isoDay = (([int]$now.DayOfWeek + 6) % 7) + 1
$thursday = $now.Date.AddDays(4 - $isoDay)
$week = [int][Math]::Floor(($thursday.DayOfYear - 1) / 7) + 1

Write-Output $now.ToString('HH:mm')
Write-Output '---'
Write-Output $now.ToString('dddd d MMMM yyyy')
Write-Output ('Week ' + $week)
