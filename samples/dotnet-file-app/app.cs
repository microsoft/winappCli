#:package Microsoft.Windows.SDK.BuildTools.WinApp@*-*
#:property OutputType=Exe
#:property TargetFramework=net10.0-windows10.0.19041.0
#:property WinAppPackageName=winapp.samples.fileapp
#:property WinAppDisplayName=winapp File-Based App Sample

using Windows.ApplicationModel;

Console.WriteLine($"Package Family Name: {Package.Current.Id.FamilyName}");
