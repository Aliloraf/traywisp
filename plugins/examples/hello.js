// <traywisp.title>Hello</traywisp.title>
// <traywisp.version>1.0</traywisp.version>
// <traywisp.author>TrayWisp contributors</traywisp.author>
// <traywisp.desc>Prints a greeting. Example plugin.</traywisp.desc>
// <traywisp.dependencies>Node.js</traywisp.dependencies>
//
// The file name has no interval (hello.js), so this plugin refreshes only on demand.

const hour = new Date().getHours();
const greeting = hour < 12 ? "Good morning" : hour < 18 ? "Good afternoon" : "Good evening";

console.log(greeting);
console.log("---");
console.log("This is an example TrayWisp plugin.");
