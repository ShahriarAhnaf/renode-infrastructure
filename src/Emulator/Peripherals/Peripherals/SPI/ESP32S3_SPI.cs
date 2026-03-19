// ESP32S3_SPI.cs -- ESP32-S3 SPI master controller for Renode
//
// Ported from capstone ESP32_SPI.cs with ESP32-S3 adaptations:
//   - Base addresses: 0x60024000 (SPI2), 0x60025000 (SPI3)
//     vs ESP32: 0x3FF64000, 0x3FF65000
//   - Data buffer base: 0x98 (ESP32-S3) vs 0x80 (ESP32)
//   - SPI_MS_DLEN_REG at 0x1C replaces separate MOSI/MISO length regs
//   - SPI_USR bit: bit 24 (ESP32-S3) vs bit 18 (ESP32) in SPI_CMD_REG
//
// Key registers (offsets from base):
//   0x00  SPI_CMD_REG        (start transaction, bit 24 = SPI_USR)
//   0x04  SPI_ADDR_REG
//   0x08  SPI_CTRL_REG
//   0x1C  SPI_MS_DLEN_REG    (combined MOSI/MISO length, bits [17:0])
//   0x98-0xD4  SPI_W0..W15_REG (data buffer, 16 words)

using System;
using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.SPI
{
    [AllowedTranslations(AllowedTranslation.ByteToDoubleWord | AllowedTranslation.WordToDoubleWord)]
    public class ESP32S3_SPI : IDoubleWordPeripheral, IKnownSize
    {
        public ESP32S3_SPI()
        {
            registers = new uint[REGISTER_COUNT];
            dataBuffer = new uint[16];
            TransactionCount = 0;
            LastMosiLength = 0;
        }

        public long Size => 0x1000;

        public int TransactionCount { get; private set; }
        public int LastMosiLength { get; private set; }

        public void Reset()
        {
            Array.Clear(registers, 0, registers.Length);
            Array.Clear(dataBuffer, 0, dataBuffer.Length);
            TransactionCount = 0;
            LastMosiLength = 0;
        }

        public uint ReadDoubleWord(long offset)
        {
            if(offset >= DATA_BUF_BASE && offset < DATA_BUF_BASE + 16 * 4)
            {
                int idx = (int)(offset - DATA_BUF_BASE) / 4;
                return dataBuffer[idx];
            }

            switch(offset)
            {
                case REG_CMD:
                    return 0;
                default:
                    int regIdx = (int)(offset / 4);
                    if(regIdx >= 0 && regIdx < REGISTER_COUNT)
                    {
                        return registers[regIdx];
                    }
                    return 0;
            }
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            if(offset >= DATA_BUF_BASE && offset < DATA_BUF_BASE + 16 * 4)
            {
                int idx = (int)(offset - DATA_BUF_BASE) / 4;
                dataBuffer[idx] = value;
                return;
            }

            int regIdx = (int)(offset / 4);
            if(regIdx >= 0 && regIdx < REGISTER_COUNT)
            {
                registers[regIdx] = value;
            }

            // ESP32-S3 uses bit 24 for SPI_USR (vs bit 18 on ESP32)
            if(offset == REG_CMD && (value & SPI_USR_BIT) != 0)
            {
                TransactionCount++;
                LastMosiLength = (int)((registers[REG_MS_DLEN / 4] & 0x3FFFF) + 1);
                registers[0] &= ~SPI_USR_BIT;
            }
        }

        private readonly uint[] registers;
        private readonly uint[] dataBuffer;

        private const int REGISTER_COUNT = 0x1000 / 4;
        private const long REG_CMD = 0x00;
        // ESP32-S3 uses combined MS_DLEN at 0x1C (ESP32 had MOSI_DLEN at 0x24)
        private const long REG_MS_DLEN = 0x1C;
        // ESP32-S3 data buffer at 0x98 (ESP32 used 0x80)
        private const long DATA_BUF_BASE = 0x98;
        // ESP32-S3 SPI_USR bit at position 24 (ESP32 used 18)
        private const uint SPI_USR_BIT = (1 << 24);
    }
}
