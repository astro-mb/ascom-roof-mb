using System.Reflection;
using System.Runtime.InteropServices;

// Le csproj positionne GenerateAssemblyInfo=false : les attributs d'assembly sont
// donc definis ici, a la main.

[assembly: AssemblyTitle("ASCOM.ArduinoRoof.Dome")]
[assembly: AssemblyDescription("Driver ASCOM Dome pour toit coulissant pilote par Arduino")]
[assembly: AssemblyCompany("astromb")]
[assembly: AssemblyProduct("Toit coulissant Arduino (Roll-Off Roof)")]
[assembly: AssemblyCopyright("Licence MIT")]
[assembly: AssemblyCulture("")]

// IMPORTANT : l'assembly n'est PAS visible COM globalement.
// Seule la classe Dome porte [ComVisible(true)], ce qui evite d'exposer
// par megarde les types internes (RoofLink, RoofState, la boite de dialogue...).
[assembly: ComVisible(false)]

// GUID de la bibliotheque de types de l'assembly.
// Distinct du GUID de la classe Dome (73b4fd55-d9d1-4163-9a43-6508d959dc27).
[assembly: Guid("0b6a4c1e-2f78-4d51-9c33-8a1e5d7b40f2")]

[assembly: AssemblyVersion("1.2.0.0")]
[assembly: AssemblyFileVersion("1.2.0.0")]
