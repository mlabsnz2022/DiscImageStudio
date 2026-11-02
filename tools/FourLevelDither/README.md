# B&W Dithering Tool

A standalone Windows image utility that converts source images into binary **0 / 255** black-and-white PNG files. Both DVD and CD recording modes use these two levels to preserve highlight and shadow details through spatial pixel dithering. The executable name remains `FourLevelDither.exe` for compatibility with existing command lines.

## Graphical Interface (GUI)

1. Launch `FourLevelDither.exe`, click **"Open Image"** or drag and drop an image file directly into the window.
2. Select **"DVD Binary"** or **"CD Binary"** to view the corresponding optical byte patterns; both options output the same 0 / 255 palette. Check **"100% Pixel View"** to inspect individual dithered pixels.
3. **"sRGB Gamma Correction"** is disabled by default (preserving perceptual luminance distribution). Enabling it automatically reprocesses the image and linearizes RGB before error diffusion.
4. Click **"Save B&W PNG"** to save the processed image. Output files default to `-BW-Dither.png` (or `-BW-Dither-sRGB.png` when gamma correction is active). The program prevents overwriting your original input file.

Switching disc modes or gamma options re-computes directly from the cached original source image, avoiding multi-generation artifacts.

Supported formats include PNG, JPEG, BMP, TIFF, and GIF. Images are exported as lossless Gray8 PNGs preserving the original pixel dimensions and DPI.

## Dithering & Optical Disc Mapping

By default, RGB channels are weighted using ITU-R BT.709 coefficients to calculate luminance, transparent areas are alpha-composited over a white background, and **serpentine Floyd–Steinberg error diffusion** is applied (alternating horizontal scan directions to reduce directional worm artifacts).

| Binary Range | PNG Gray Value | DVD Scrambler Q-Byte | CD Byte |
| --- | ---: | --- | --- |
| 0–127 | 0 (Black) | `0xA5` | `0x0D` |
| 128–255 | 255 (White) | `0x92` | `0xAA` |

With gamma correction off, a uniform 50% gray area produces ~50.2% white pixels. Output files strictly contain only values `0` and `255`.

### sRGB Gamma Correction

When enabled, RGB channels are decoded using the standard sRGB transfer function into linear optical intensity before Rec.709 luminance weighting and error diffusion. This changes the distribution of black and white pixels (typically producing darker, richer midtones that closer reflect physical light integration).

## Command Line (CLI)

```powershell
# Headless conversion (defaults to DVD binary); output must be a new .png file
.\FourLevelDither.exe --input original.jpg --output result.png

# Explicitly choose DVD or CD mode (both output 0/255 binary PNG)
.\FourLevelDither.exe --input original.jpg --output dvd-result.png --mode dvd
.\FourLevelDither.exe --input original.jpg --output cd-result.png --mode cd

# Compare with and without sRGB gamma linearization
.\FourLevelDither.exe --input original.jpg --output result-none.png --gamma none
.\FourLevelDither.exe --input original.jpg --output result-srgb.png --gamma srgb

# Launch GUI with an initial image preloaded
.\FourLevelDither.exe original.jpg

# Display help
.\FourLevelDither.exe --help
```

## Building from Source

```powershell
# Run directly with .NET 9
dotnet run --project tools/FourLevelDither --configuration Release

# Publish self-contained single-file executable
dotnet publish tools/FourLevelDither/FourLevelDither.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish/tools
```
