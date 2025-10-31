{
  "targets": [
    {
      "target_name": "{addon-name}",
      "sources": ["{addon-name}.cc"],
      "include_dirs": [
        "<!@(node -p \"require('node-addon-api').include\")",
        "<!(node -e \"require('nan')\")",
        "<!@(node -p \"require('@microsoft/winappcli').getLocalWinappPath().replace(/\\\\/g, '/') + '/include'\")"
      ],
      "msvs_settings": {
        "VCCLCompilerTool": {
          "ExceptionHandling": 1,
          "DebugInformationFormat": "OldStyle",
          "AdditionalOptions": [
            "/FS"
          ]
        },
        "VCLinkerTool": {
          "GenerateDebugInformation": "true"
        }
      },
      "defines": [
        "NODE_ADDON_API_CPP_EXCEPTIONS",
        "WINVER=0x0A00",
        "_WIN32_WINNT=0x0A00"
      ],
      "library_dirs": [
        "<!@(node -p \"require('@microsoft/winappcli').getLocalWinappPath().replace(/\\\\/g, '/') + '/lib/<(target_arch)'\")",
        "../build/<(target_arch)/Release"
      ],
      "libraries": [
        "comctl32.lib",
        "shcore.lib",
        "WindowsApp.lib",
        "Microsoft.WindowsAppRuntime.Bootstrap.lib"
      ],
      "dependencies": [
        "<!(node -p \"require('node-addon-api').gyp\")"
      ],
    }
  ]
}