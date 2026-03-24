# FsHotWatch.Fantomas

FsHotWatch plugin that checks if source files are properly formatted
using Fantomas. Subscribes to file change events and reports unformatted files.

## Usage

```fsharp
let plugin = FormatCheckPlugin()
daemon.Register(plugin)

// Query unformatted files
// fs-hot-watch unformatted
```
