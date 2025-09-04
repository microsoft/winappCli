{
  "targets": [
    {
      "target_name": "{addon-name}",
      "sources": ["{addon-name}.cc"],
      "include_dirs": [
        "<!@(node -p \"require('node-addon-api').include\")",
        "<!(node -e \"require('nan')\")",
        "<!@(node -p \"require('windows-sdks').getNugetPackagePath('Microsoft.WindowsAppSDK').replace(/\\\\/g, '/') + '/include'\")",
        "<!@(node -p \"require('path').join(require('path').dirname(require.resolve('windows-sdks')), 'generated', 'include').replace(/\\\\/g, '/')\")",
      ],
      "defines": [ "NAPI_DISABLE_CPP_EXCEPTIONS" ],
      "library_dirs": [
        "<!@(node -p \"require('windows-sdks').getNugetPackagePath('Microsoft.WindowsAppSDK').replace(/\\\\/g, '/') + '/lib/win10-<(target_arch)'\")",
        "../build/<(target_arch)/Release"
      ],
      "libraries": [
        "WindowsApp.lib",
        "Microsoft.WindowsAppRuntime.Bootstrap.lib"
      ]
    }
  ]
}