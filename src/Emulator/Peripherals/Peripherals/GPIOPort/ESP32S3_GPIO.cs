// ESP32S3_GPIO.cs -- ESP32-S3 GPIO controller for Renode
//
// Ported from capstone ESP32_GPIO.cs with ESP32-S3 adaptations:
//   - 49 GPIO pins (GPIO0-GPIO48) vs ESP32's 40
//   - GPIO_PINn_REG base offset: 0x74 (ESP32-S3) vs 0x88 (ESP32)
//   - GPIO_FUNCn_IN_SEL_CFG_REG: 0x154 (ESP32-S3) vs 0x130 (ESP32)
//   - GPIO_FUNCn_OUT_SEL_CFG_REG: 0x554 (ESP32-S3) vs 0x530 (ESP32)
//   - Base address: 0x60004000 (ESP32-S3) vs 0x3FF44000 (ESP32)
//
// Key registers (offsets from 0x60004000):
//   0x04  GPIO_OUT_REG       (output value, pins 0-31)
//   0x10  GPIO_OUT1_REG      (output value, pins 32-48)
//   0x20  GPIO_ENABLE_REG    (output enable, pins 0-31)
//   0x2C  GPIO_ENABLE1_REG   (output enable, pins 32-48)
//   0x38  GPIO_STRAP_REG     (boot strapping pins)
//   0x3C  GPIO_IN_REG        (input value, pins 0-31)
//   0x40  GPIO_IN1_REG       (input value, pins 32-48)
//   0x44+ GPIO_STATUS_REG    (interrupt status)
//   Pin config: 0x74 + pin*4  GPIO_PINn_REG (per-pin config)

using System;
using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.GPIOPort;

namespace Antmicro.Renode.Peripherals.GPIOPort
{
    [AllowedTranslations(AllowedTranslation.ByteToDoubleWord | AllowedTranslation.WordToDoubleWord)]
    public class ESP32S3_GPIO : BaseGPIOPort, IDoubleWordPeripheral, IKnownSize
    {
        public ESP32S3_GPIO(IMachine machine, int numberOfPins = 49) : base(machine, numberOfPins)
        {
            pinConfig = new uint[numberOfPins];
            outputValue0 = 0;
            outputValue1 = 0;
            outputEnable0 = 0;
            outputEnable1 = 0;
            inputOverride0 = 0;
            inputOverride1 = 0;
            inputOverrideMask0 = 0;
            inputOverrideMask1 = 0;
            strapValue = 0x0000000C;
        }

        public long Size => 0x1000;

        public void SetInputPin(int pin, bool level)
        {
            if(pin < 32)
            {
                uint mask = (uint)(1 << pin);
                inputOverrideMask0 |= mask;
                if(level)
                {
                    inputOverride0 |= mask;
                }
                else
                {
                    inputOverride0 &= ~mask;
                }
            }
            else if(pin < 49)
            {
                uint mask = (uint)(1 << (pin - 32));
                inputOverrideMask1 |= mask;
                if(level)
                {
                    inputOverride1 |= mask;
                }
                else
                {
                    inputOverride1 &= ~mask;
                }
            }
        }

        public override void Reset()
        {
            base.Reset();
            Array.Clear(pinConfig, 0, pinConfig.Length);
            outputValue0 = 0;
            outputValue1 = 0;
            outputEnable0 = 0;
            outputEnable1 = 0;
            inputOverride0 = 0;
            inputOverride1 = 0;
            inputOverrideMask0 = 0;
            inputOverrideMask1 = 0;
        }

        public uint ReadDoubleWord(long offset)
        {
            switch(offset)
            {
                case REG_OUT:
                    return outputValue0;
                case REG_OUT1:
                    return outputValue1;
                case REG_OUT_W1TS:
                case REG_OUT_W1TC:
                case REG_OUT1_W1TS:
                case REG_OUT1_W1TC:
                    return 0;
                case REG_ENABLE:
                    return outputEnable0;
                case REG_ENABLE1:
                    return outputEnable1;
                case REG_STRAP:
                    return strapValue;
                case REG_IN:
                    return GetInputValue0();
                case REG_IN1:
                    return GetInputValue1();
                case REG_STATUS:
                case REG_STATUS1:
                    return 0;
                default:
                    if(offset >= REG_PIN_BASE && offset < REG_PIN_BASE + 49 * 4)
                    {
                        int pin = (int)(offset - REG_PIN_BASE) / 4;
                        return pinConfig[pin];
                    }
                    if(offset >= REG_FUNC_IN_BASE && offset < REG_FUNC_IN_BASE + 256 * 4)
                    {
                        return 0;
                    }
                    if(offset >= REG_FUNC_OUT_BASE && offset < REG_FUNC_OUT_BASE + 49 * 4)
                    {
                        return 0;
                    }
                    return 0;
            }
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            switch(offset)
            {
                case REG_OUT:
                    outputValue0 = value;
                    break;
                case REG_OUT_W1TS:
                    outputValue0 |= value;
                    break;
                case REG_OUT_W1TC:
                    outputValue0 &= ~value;
                    break;
                case REG_OUT1:
                    outputValue1 = value;
                    break;
                case REG_OUT1_W1TS:
                    outputValue1 |= value;
                    break;
                case REG_OUT1_W1TC:
                    outputValue1 &= ~value;
                    break;
                case REG_ENABLE:
                    outputEnable0 = value;
                    break;
                case REG_ENABLE_W1TS:
                    outputEnable0 |= value;
                    break;
                case REG_ENABLE_W1TC:
                    outputEnable0 &= ~value;
                    break;
                case REG_ENABLE1:
                    outputEnable1 = value;
                    break;
                case REG_ENABLE1_W1TS:
                    outputEnable1 |= value;
                    break;
                case REG_ENABLE1_W1TC:
                    outputEnable1 &= ~value;
                    break;
                case REG_STATUS_W1TC:
                case REG_STATUS1_W1TC:
                    break;
                default:
                    if(offset >= REG_PIN_BASE && offset < REG_PIN_BASE + 49 * 4)
                    {
                        int pin = (int)(offset - REG_PIN_BASE) / 4;
                        pinConfig[pin] = value;
                    }
                    break;
            }
        }

        private uint GetInputValue0()
        {
            uint val = outputValue0 & outputEnable0;
            val = (val & ~inputOverrideMask0) | (inputOverride0 & inputOverrideMask0);
            return val;
        }

        private uint GetInputValue1()
        {
            uint val = outputValue1 & outputEnable1;
            val = (val & ~inputOverrideMask1) | (inputOverride1 & inputOverrideMask1);
            return val;
        }

        private readonly uint[] pinConfig;
        private uint outputValue0;
        private uint outputValue1;
        private uint outputEnable0;
        private uint outputEnable1;
        private uint inputOverride0;
        private uint inputOverride1;
        private uint inputOverrideMask0;
        private uint inputOverrideMask1;
        private readonly uint strapValue;

        // ESP32-S3 GPIO register offsets (differ from ESP32)
        private const long REG_OUT = 0x04;
        private const long REG_OUT_W1TS = 0x08;
        private const long REG_OUT_W1TC = 0x0C;
        private const long REG_OUT1 = 0x10;
        private const long REG_OUT1_W1TS = 0x14;
        private const long REG_OUT1_W1TC = 0x18;
        private const long REG_ENABLE = 0x20;
        private const long REG_ENABLE_W1TS = 0x24;
        private const long REG_ENABLE_W1TC = 0x28;
        private const long REG_ENABLE1 = 0x2C;
        private const long REG_ENABLE1_W1TS = 0x30;
        private const long REG_ENABLE1_W1TC = 0x34;
        private const long REG_STRAP = 0x38;
        private const long REG_IN = 0x3C;
        private const long REG_IN1 = 0x40;
        private const long REG_STATUS = 0x44;
        private const long REG_STATUS_W1TC = 0x4C;
        private const long REG_STATUS1 = 0x50;
        private const long REG_STATUS1_W1TC = 0x58;
        // ESP32-S3: pin config starts at 0x74 (ESP32 used 0x88)
        private const long REG_PIN_BASE = 0x74;
        // ESP32-S3: func_in starts at 0x154 (ESP32 used 0x130)
        private const long REG_FUNC_IN_BASE = 0x154;
        // ESP32-S3: func_out starts at 0x554 (ESP32 used 0x530)
        private const long REG_FUNC_OUT_BASE = 0x554;
    }
}
