# Zpracování obrázků z URL – spuštění

Webová aplikace pro hromadné zpracování produktových fotografií z URL adres.

## Požadavky

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

Ověření instalace:
```
dotnet --version
```

## Spuštění

```
cd img_app
dotnet run
```

Aplikace se spustí na **http://localhost:5001** (port je nastaven v `appsettings.json`).

## Závislosti (NuGet)

Stáhnou se automaticky při prvním `dotnet run`:

| Balíček | Účel |
|---------|------|
| `SixLabors.ImageSharp` 3.1.x | Zpracování obrázků |
| `FluentFTP` 51.x | FTP/FTPS klient |

> **Licence ImageSharp:** Six Labors Split License 1.0 – zdarma pro projekty s příjmem pod 1 M USD/rok.

## Funkce

- Načtení URL obrázků z HTML, XML/JSON feedu nebo prostého seznamu
- Ořez bílého/průhledného pozadí (nulová tolerance)
- Složení průhlednosti na bílé pozadí
- Změna velikosti (Lanczos3, nikdy nezvětšuje)
- Přidání bílého okraje
- Úprava jasu
- Výstup: PNG nebo WebP (bezztrátový), JPEG (ztrátový)
- Stažení jako ZIP, base64, nebo přímý upload na FTP/FTPS
- Integrovaný FTP správce s drag-and-drop

## Struktura projektu

```
img_app/
  Program.cs          – HTTP endpointy (/process, /parse, /ftp/*)
  ImageProcessor.cs   – pipeline zpracování obrázků
  appsettings.json    – konfigurace portu
  wwwroot/
    index.html        – webové rozhraní
```
