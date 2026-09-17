using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// General Information about an assembly is controlled through the following 
// set of attributes. Change these attribute values to modify the information
// associated with an assembly.
[assembly: AssemblyTitle("Canon Astro Image")]
[assembly: AssemblyDescription("Removes the Canon RAW CR2/CR3 only image save limitation when using the native Canon driver. (FITS, XISF, TIFF) with full metadata")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("Gus Sanchez")]
[assembly: AssemblyProduct("Canon Astro Image")]
[assembly: AssemblyCopyright("Copyright © 2025 - MIT License")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]

// Setting ComVisible to false makes the types in this assembly not visible 
// to COM components.  If you need to access a type in this assembly from 
// COM, set the ComVisible attribute to true on that type.
[assembly: ComVisible(false)]

// The following GUID is for the ID of the typelib if this project is exposed to COM
[assembly: Guid("a1b2c3d4-e5f6-4a5b-8c9d-1e2f3a4b5c6d")]

// Version information for an assembly consists of the following four values:
//
//      Major Version
//      Minor Version 
//      Build Number
//      Revision
//
// You can specify all the values or you can default the Build and Revision Numbers 
// by using the '*' as shown below:
// [assembly: AssemblyVersion("1.0.*")]
[assembly: AssemblyVersion("1.4.0.0")]
[assembly: AssemblyFileVersion("1.4.0.0")]

// Plugin metadata
[assembly: AssemblyMetadata("MinimumApplicationVersion", "3.0.0.0")]
// Logo shown by NINA at the top right of the plugin page and as the icon in the plugin list.
// NINA loads it from this URL, so it must be reachable: the PNG lives in this repo on GitHub.
[assembly: AssemblyMetadata("FeaturedImageURL", "https://raw.githubusercontent.com/gsanchez006/canon_nina/main/Assets/logo-512.png")]
[assembly: AssemblyMetadata("LongDescription", "Removes the Canon RAW CR2/CR3 only image save limitation when using the native Canon driver. (FITS, XISF, TIFF) native support with proper metadata instead of saving only Canon proprietary CR2/CR3 files")]
