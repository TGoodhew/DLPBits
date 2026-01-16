# DLPBits

The console app takes the SDRAM dump image from KO4BB and uses NI-VISA to program an 8560E Spectrum Analyzer and its 85620A Mass Memory Module with the DLPs

You can get the image from https://www.ko4bb.com/getsimple/index.php?id=manuals&dir=HP_Agilent_Keysight/HP_85620A

**Drop the .BIN file into the debug folder or the folder that the .EXE lives in**

Much, much thanks to https://github.com/KIrill-ka for the code to decode the image address and data bits.

## Prerequisites

Before you can use DLPBits, ensure you have the following:

- **NI-VISA** - National Instruments VISA (Virtual Instrument Software Architecture) must be installed for GPIB communication
- **.NET Framework 4.7.2** or later
- **HP 8560E Spectrum Analyzer** with **85620A Mass Memory Module**
- **GPIB interface** (e.g., NI GPIB-USB-HS adapter or similar)
- **SRAM_85620A.bin** file from [KO4BB](https://www.ko4bb.com/getsimple/index.php?id=manuals&dir=HP_Agilent_Keysight/HP_85620A)

## Installation

1. **Clone the repository**
   ```bash
   git clone https://github.com/TGoodhew/DLPBits.git
   cd DLPBits
   ```

2. **Restore NuGet packages**
   ```bash
   nuget restore DLPBits.sln
   ```
   Or in Visual Studio, right-click the solution and select "Restore NuGet Packages"

3. **Build the solution**
   - Open `DLPBits.sln` in Visual Studio
   - Build the solution (Build → Build Solution or press F6)
   - Alternatively, use MSBuild from the command line:
     ```bash
     msbuild DLPBits.sln /p:Configuration=Release
     ```

## Usage

1. **Prepare the environment**
   - Download the `SRAM_85620A.bin` file from KO4BB
   - Place the .BIN file in the same folder as the DLPBits executable (or in the debug folder during development)

2. **Connect your hardware**
   - Connect the HP 8560E Spectrum Analyzer via GPIB interface
   - Ensure the GPIB address is set correctly (default is 18)
   - Verify the 85620A Mass Memory Module is installed

3. **Run the application**
   ```bash
   DLPBits.exe
   ```

4. **Follow the interactive menu**
   - **Set GPIB Address** - Configure the GPIB address for your spectrum analyzer (default: 18)
   - **Read ROM** - Read and parse the SDRAM dump image file (SRAM_85620A.bin)
   - **Clear Mass Memory** - Clear existing data from the Mass Memory Module (optional)
   - **Create DLPs** - Program the DLPs to the Mass Memory Module
   - **Exit** - Close the application

## Setup Process

The following image shows the basic process for setting up both the Phase Noise and Spur Utilities once you have used DLPBits to copy them to the Mass Memory Module.

![SetupImage](Image/PHSetup.png?raw=true)

## Dependencies

This project uses the following libraries and frameworks:

- **[Spectre.Console](https://spectreconsole.net/)** (v0.51.1) - Rich console UI library
- **NI-VISA** (v25.5.0.13) - National Instruments VISA implementation
- **[Ivi.Visa](https://www.ivifoundation.org/)** (v8.0.0.0) - IVI Foundation VISA specification
- **.NET Framework 4.7.2**

Additional dependencies:
- System.Buffers (v4.6.1)
- System.Memory (v4.6.3)
- System.Numerics.Vectors (v4.6.1)
- System.Runtime.CompilerServices.Unsafe (v6.1.2)

## Contributing

Contributions are welcome! If you'd like to contribute to DLPBits:

1. Fork the repository
2. Create a feature branch (`git checkout -b feature/your-feature`)
3. Commit your changes (`git commit -am 'Add some feature'`)
4. Push to the branch (`git push origin feature/your-feature`)
5. Open a Pull Request

Please ensure your code follows the existing style and includes appropriate comments where necessary.

## License

This project is licensed under the MIT License - see the [LICENSE.txt](LICENSE.txt) file for details.

## Authors and Acknowledgments

- **Tony Goodhew** (tony@schnauzergroup.com) - Original author and maintainer
- **[KIrill-ka](https://github.com/KIrill-ka)** - For providing the critical address translation algorithm to decode the SDRAM image (EEVBlog user: https://www.eevblog.com/forum/profile/?u=127220)
- **[KO4BB](https://www.ko4bb.com/)** - For providing the SDRAM dump image files

Special thanks to the EEVBlog community for their support and contributions to this project.
