﻿using System;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Forms;
using Diz.Core;
using Diz.Core.model;
using Diz.Core.util;
using DiztinGUIsh.controller;

namespace DiztinGUIsh.window.dialog
{
    public partial class MarkManyView : Form, IMarkManyView
    {
        public IMarkManyController Controller { get; set; }
        private IReadOnlySnesRomBase Data => Controller.Data;
        public int Property => property.SelectedIndex;
        private int PropertyMaxVal => Property == 1 ? 0x100 : 0x10000;

        public int Column
        {
            set
            {
                UpdatePropertyIndex(value);
                UpdateMostUi();
                UpdateTextUi();
            }
        }
        private Util.NumberBase NoBase => 
            radioDec.Checked ? Util.NumberBase.Decimal : Util.NumberBase.Hexadecimal;
        private int DigitCount => NoBase == Util.NumberBase.Hexadecimal && radioROM.Checked ? 6 : 0;
        
        private int PropertyValue => 
            property.SelectedIndex == 1 ? 
                Data.GetDataBank(Controller.DataRange.StartIndex) : 
                Data.GetDirectPage(Controller.DataRange.StartIndex);
        
        private int propertyValue;
        private bool updatingText;

        /// <summary>
        /// Dialog that lets us mark many of a particular column on the data grid form
        /// </summary>
        /// <param name="column">Which column we're marking many of (determines UI elements)</param>
        /// <param name="data">Rom we would be marking the data against</param>
        public MarkManyView()
        {
            InitializeComponent();
            InitCombos();
        }

        private void InitCombos()
        {
            flagCombo.SelectedIndex = 3;
            archCombo.SelectedIndex = 0;
            mxCombo.SelectedIndex = 0;
        }

        private void UpdatePropertyIndex(int column)
        {
            // TODO: woof. fixme :)
            property.SelectedIndex = column switch
            {
                8 => 1,
                9 => 2,
                10 => 3,
                11 => 4,
                _ => 0
            };
        }
        
        private void ClampPropertyValue() => 
            propertyValue = Util.ClampIndex(propertyValue, PropertyMaxVal);

        public object GetFinalValue() {
            switch (property.SelectedIndex)
            {
                case 0:
                    switch (flagCombo.SelectedIndex)
                    {
                        case 0: return FlagType.Unreached;
                        case 1: return FlagType.Opcode;
                        case 2: return FlagType.Operand;
                        case 3: return FlagType.Data8Bit;
                        case 4: return FlagType.Graphics;
                        case 5: return FlagType.Music;
                        case 6: return FlagType.Empty;
                        case 7: return FlagType.Data16Bit;
                        case 8: return FlagType.Pointer16Bit;
                        case 9: return FlagType.Data24Bit;
                        case 10: return FlagType.Pointer24Bit;
                        case 11: return FlagType.Data32Bit;
                        case 12: return FlagType.Pointer32Bit;
                        case 13: return FlagType.Text;
                    }

                    break;
                case 1:
                case 2:
                    return propertyValue;
                case 3:
                case 4:
                    return mxCombo.SelectedIndex != 0;
                case 5:
                    switch (archCombo.SelectedIndex)
                    {
                        case 0: return Architecture.Cpu65C816;
                        case 1: return Architecture.Apuspc700;
                        case 2: return Architecture.GpuSuperFx;
                    }

                    break;
                default:
                    return 0;
            }
            
            return 0;
        }

        public bool PromptDialog() => ShowDialog() == DialogResult.OK;

        private void UpdateMostUi()
        {
            flagCombo.Visible = property.SelectedIndex == 0;
            regValue.Visible = property.SelectedIndex == 1 || property.SelectedIndex == 2;
            mxCombo.Visible = property.SelectedIndex == 3 || property.SelectedIndex == 4;
            archCombo.Visible = property.SelectedIndex == 5;
            
            propertyValue = PropertyValue;
            regValue.MaxLength = property.SelectedIndex == 1 ? 3 : 5;
        }

        private void UpdateTextUi(TextBox selected = null)
        {
            ClampPropertyValue();
            
            updatingText = true;
            if (selected != textStart) UpdateStartText();
            if (selected != textEnd) UpdateEndText();
            if (selected != textCount) UpdateCountText();
            if (selected != regValue) UpdateRegValueText();
            updatingText = false;
        }

        private void UpdateRegValueText() => 
            regValue.Text = Util.NumberToBaseString(propertyValue, NoBase, 0);

        private void UpdateCountText() => 
            textCount.Text = Util.NumberToBaseString(Controller.DataRange.RangeCount, NoBase, 0);

        private void UpdateEndText() => 
            textEnd.Text = Util.NumberToBaseString(radioROM.Checked ? Data.ConvertPCtoSnes(Controller.DataRange.EndIndex) : Controller.DataRange.EndIndex, NoBase, DigitCount);

        private void UpdateStartText() =>
            textStart.Text =
                Util.NumberToBaseString(radioROM.Checked ? Data.ConvertPCtoSnes(Controller.DataRange.StartIndex) : Controller.DataRange.StartIndex, NoBase, DigitCount);

        private void property_SelectedIndexChanged(object sender, EventArgs e) => UpdateMostUi();

        private bool IsRomAddress => radioROM.Checked;
        private int ConvertToRomOffsetIfNeeded(int v) => IsRomAddress ? Data.ConvertSnesToPc(v) : v;
        
        private void regValue_TextChanged(object sender, EventArgs e)
        {
            var style = radioDec.Checked ? NumberStyles.Number : NumberStyles.HexNumber;

            if (!int.TryParse(regValue.Text, style, null, out var result)) 
                return;
            
            propertyValue = result;
        }
        
        private void OnTextChanged(TextBox textBox, Action<int> onResult)
        {        
            if (updatingText)
                return;

            updatingText = true;
            var style = radioDec.Checked ? NumberStyles.Number : NumberStyles.HexNumber;

            if (int.TryParse(textBox.Text, style, null, out var result))
                onResult(result);
            
            UpdateTextUi(textBox);
        }
        
        
        private void radioHex_CheckedChanged(object sender, EventArgs e) => UpdateTextUi();
        private void radioROM_CheckedChanged(object sender, EventArgs e) => UpdateTextUi();

        private void okay_Click(object sender, EventArgs e) => DialogResult = DialogResult.OK;
        private void cancel_Click(object sender, EventArgs e) => Close();

        
        #region Range Actual Updates

        private void textCount_TextChanged(object sender, EventArgs e) => 
            OnTextChanged(textCount, newCount =>
            {
                SetRangeValuesManually(Controller.DataRange, 
                    -1, -1, newCount);
            });

        private void textEnd_TextChanged(object sender, EventArgs e) =>
            OnTextChanged(textEnd, newEndIndex =>
            {
                SetRangeValuesManually(Controller.DataRange, 
                    -1, ConvertToRomOffsetIfNeeded(newEndIndex), -1);
            });

        private void textStart_TextChanged(object sender, EventArgs e) => 
            OnTextChanged(textStart, newStartIndex =>
            {
                SetRangeValuesManually(Controller.DataRange, 
                    ConvertToRomOffsetIfNeeded(newStartIndex), -1, -1);
            });

        public static void SetRangeValuesManually(IDataRange dataRange, int newStartIndex = -1, int newEndIndex = -1, int newCount = -1)
        {
            if (dataRange == null)
                return;
            
            // info: "DataRange" has a start, end, and a count. if you change one,
            // the other 2 reflect that change. neat. however, the implementation ends up
            // not being the most UX friendly so, we're going to control it more directly here.
            //
            // let's do everything in terms of the StartIndex

            var oldEndIndex = dataRange.EndIndex;
            var oldStartIndex = dataRange.StartIndex;

            if (newStartIndex != -1)
            {
                Debug.Assert(newEndIndex == -1 && newCount == -1);

                // if changing START.  leave END, change # of bytes.
                var updatedCount = oldEndIndex - newStartIndex + 1;
                if (updatedCount < 0)
                    updatedCount = 1;

                dataRange.ManualUpdate(newStartIndex, updatedCount);
            } 
            else if (newEndIndex != -1)
            {
                //if changing END, leave START, change # of bytes.
                Debug.Assert(newCount == -1);
                
                var updatedCount = newEndIndex - oldStartIndex + 1;
                if (updatedCount < 0)
                    updatedCount = 1;
                
                dataRange.ManualUpdate(oldStartIndex, updatedCount);
            }
            else if (newCount != -1)
            {
                // if changing # bytes, leave START, change END
                // var updatedEndIndex = oldStartIndex + (newCount - 1);

                dataRange.ManualUpdate(oldStartIndex, newCount);
            }
        }
        #endregion
    }
}
