# DLPBits

The console app takes the SDRAM dump image from KO4BB and uses NI-VISA to program an 8560E Spectrum Analyzer and its 85620A Mass Memory Module with the DLPs

You can get the image from https://www.ko4bb.com/getsimple/index.php?id=manuals&dir=HP_Agilent_Keysight/HP_85620A

**Drop the .BIN file into the debug folder or the folder that the .EXE lives in**

Much, much thanks to https://github.com/KIrill-ka for the code to decode the image address and data bits.

## Setup process

The following image shows the basic process for setting up both the Phase Noise and Spur Utilities once you have used DLPBits to copy them to the Mass Memory Module.

![SetupImage](Image/PHSetup.png?raw=true)
