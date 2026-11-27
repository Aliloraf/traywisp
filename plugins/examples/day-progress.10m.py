#!/usr/bin/env python3
# <xbar.title>Day progress</xbar.title>
# <xbar.version>v1.0</xbar.version>
# <xbar.author>TrayWisp contributors</xbar.author>
# <xbar.desc>Shows how much of the day has passed. Example plugin; TrayWisp cannot run plugins yet.</xbar.desc>
# <xbar.dependencies>python3</xbar.dependencies>
#
# Uses xbar-style metadata tags, which TrayWisp also reads.
# The file name sets a ten-minute refresh interval: day-progress.10m.py.

from datetime import datetime

now = datetime.now()
seconds = now.hour * 3600 + now.minute * 60 + now.second
percent = seconds * 100 // 86400

print(f"{percent}% of today")
print("---")
print(f"{24 - now.hour - 1} h {59 - now.minute} min left")
