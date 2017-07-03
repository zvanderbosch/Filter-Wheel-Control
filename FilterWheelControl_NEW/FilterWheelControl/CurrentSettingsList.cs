using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows; // for MessageBox
using System.Collections.ObjectModel; // for ObservableCollection
using System.IO;

using PrincetonInstruments.LightField.AddIns;

namespace FilterWheelControl
{
    public class FilterSetting
    {
        public string FilterType { get; set; }
        public double DisplayTime { get; set; }
        public double UserInputTime { get; set; }
        public double SlewAdjustedTime { get; set; }
        public int NumExposures { get; set; }
        public int OrderLocation { get; set; }
        public FilterSetting Next { get; set; }
    }
    
    public class CurrentSettingsList
    {
        #region Static Variables

        // The time subtracted from all integer-second exposure times, in seconds, when the Adjust Exposure Times for Trigger box is selected
        private static readonly double TRIGGER_SLEW_CORRECTION = 0.005; // seconds

        #endregion // Static Variables

        #region Instance Variables

        private ObservableCollection<FilterSetting> _filter_settings; // The structure holding the list of filter settings.
        private readonly object _current_settings_lock; // The lock for adding, editing, and deleting from the list, as well as retrieving.
        private WheelInterface _wheel_interface; // The wheel interface object associated with this settings list.

        #endregion // Instance Variables

        #region Constructors

        /// <summary>
        /// Instantiate a new CurrentSettingsList object and set the initial settings list to be empty
        /// </summary>
        public CurrentSettingsList(WheelInterface wi)
        {
            this._filter_settings = new ObservableCollection<FilterSetting>();
            
            this._current_settings_lock = new object();
            this._wheel_interface = wi;
        }

        /// <summary>
        /// Instantiate a new CurrentSettingsList object and set the initial settings list to a pre-existing list
        /// </summary>
        /// <param name="settings">The settings to set as the CurrentSettingsList filter settings</param>
        public CurrentSettingsList(ObservableCollection<FilterSetting> settings)
        {
            this._filter_settings = settings;
            this._current_settings_lock = new object();
        }


        #endregion // Constructors

        #region Accessors

        /// <summary>
        /// Accessor for the filter settings ObservableCollection
        /// </summary>
        /// <returns>The ObservableCollection holding all of the Filter objects</returns>
        public ObservableCollection<FilterSetting> GetSettingsCollection() { return _filter_settings; }

        /// <summary>
        /// Build the file contents of a filter settings file.
        /// Format is:  FilterType\tUserInputTime\tNumExposures\r\n
        /// </summary>
        /// <returns>A string holding the contents of the file</returns>
        public string GenerateFileContent()
        {
            string content = "";
            lock (_current_settings_lock)
            {
                foreach (FilterSetting f in _filter_settings)
                {
                    content += f.FilterType + '\t' + f.UserInputTime + '\t' + f.NumExposures + "\r\n";
                }
            }

            return content;
        }
   
        
        /// <summary>
        /// Connects all the Next properties of the FilterSettings to the next filter, and inserts transition frames when necessary.
        /// </summary>
        /// <returns>The first filter setting in the sequence.</returns>
        public FilterSetting GetCaptureSettings() 
        {
            lock (_current_settings_lock)
            {
                // Update the Next value for filters 0-(n-1)
                for (int i = 1; i < _filter_settings.Count; i++)
                {
                    FilterSetting cur = _filter_settings[i - 1];
                    FilterSetting next = _filter_settings[i];

                    // If the current filter is not the same as the next filter, we need to insert a transition frame
                    if (cur.FilterType != next.FilterType)
                    {

                        FilterSetting transit = new FilterSetting
                        {
                            FilterType = next.FilterType,
                            DisplayTime = WheelInterface.TimeBetweenFilters(cur.FilterType, next.FilterType),
                            UserInputTime = 0,
                            SlewAdjustedTime = 0,
                            NumExposures = 1,
                            OrderLocation = -1
                        };
                        cur.Next = transit;
                        transit.Next = next;
                    }
                    else
                    {
                        cur.Next = next;
                    }
                }

                // Update the Next value for filter n (back to 0).
                FilterSetting last = _filter_settings[_filter_settings.Count - 1];
                FilterSetting first = _filter_settings[0];

                // If the n to 0 transition requires rotation, add the transition frame.
                if (last.FilterType != first.FilterType)
                {

                    FilterSetting transit = new FilterSetting
                    {
                        FilterType = first.FilterType,
                        DisplayTime = WheelInterface.TimeBetweenFilters(last.FilterType, first.FilterType),
                        UserInputTime = 0,
                        SlewAdjustedTime = 0,
                        NumExposures = 1,
                        OrderLocation = -1
                    };
                    
                    last.Next = transit;
                    transit.Next = first;
                }
                else
                {
                    last.Next = first;
                }
                
            }

            // Return the first filter setting.
            return _filter_settings[0];
        }

        /// <summary>
        /// Calcualtes the time spent in transition for a sequence, including the transition from n to 0.
        /// </summary>
        /// <returns>The time, in seconds, spent transitioning.</returns>
        public double CalculateTransitionTime()
        {
            double total_transit_time = 0;

            if (_filter_settings.Count > 0)
            {
                // calculate the 0 to n transitions
                for (int i = 1; i < _filter_settings.Count; i++)
                {
                    if (_filter_settings[i - 1].FilterType != _filter_settings[i].FilterType)
                        total_transit_time += WheelInterface.TimeBetweenFilters(_filter_settings[i - 1].FilterType, _filter_settings[i].FilterType);
                }

                // calculate the n to 0 transition
                if (_filter_settings[_filter_settings.Count - 1].FilterType != _filter_settings[0].FilterType)
                {
                    total_transit_time += WheelInterface.TimeBetweenFilters(_filter_settings[_filter_settings.Count - 1].FilterType, _filter_settings[0].FilterType);
                }
            }

            return total_transit_time;
        }

        /// <summary>
        /// Calculates the total time the camera is taking exposures.
        /// </summary>
        /// <returns>The time, in seconds, that is spent exposing.</returns>
        public double CalculateExposedTime()
        {
            double total_exposing_time = 0;

            foreach (FilterSetting f in _filter_settings)
            {
                total_exposing_time += f.DisplayTime * f.NumExposures;
            }

            return total_exposing_time;
        }

        /// <summary>
        /// Calculates the number of frames, including transition frames, per sequence.  Includes the n-0 transition.
        /// </summary>
        /// <returns>The number of frames, including transitions, per sequence.</returns>
        public int FramesPerCycle()
        {
            // Start with the initial number of frames
            int frames = 0;

            // Add all transitions
            for (int i = 1; i < _filter_settings.Count; i++)
            {
                frames += _filter_settings[i].NumExposures;
                if (_filter_settings[i].FilterType != _filter_settings[i - 1].FilterType)
                    frames++;
            }

            // Add the zero frame
            frames += _filter_settings[0].NumExposures;

            // Add the n-0 transition if necessary
            if (_filter_settings[_filter_settings.Count - 1].FilterType != _filter_settings[0].FilterType)
                frames++;

            return frames;
        }

        /// <summary>
        /// Retrieves the value of the TRIGGER_SLEW_CORRECTION variable.
        /// </summary>
        /// <returns>TRIGGER_SLEW_CORRECTION</returns>
        public double GetTriggerSlewCorrection()
        {
            return TRIGGER_SLEW_CORRECTION;
        }

        #endregion // Accessors

        #region Modifiers

        /// <summary>
        /// Adds a Filter with the specified settings to the ObservableCollection _FILTER_SETTINGS
        /// </summary>
        /// <param name="filterType">The string holding the type of filter</param>
        /// <param name="time">A string holding the time, in seconds, of the exposure duration</param>
        /// <param name="frames">A string holding the number of consecutive frames of this exposure time and filter to take</param>
        /// <param name="slewAdjust">True if the times should be adjusted to account for trigger timing slew, false otherwise</param>
        /// <returns>true if the object was added, false otherwise</returns>
        public bool Add(object filterType, string time, string frames, bool slewAdjust)
        {
            // If the input time is valid, calculate slew adjusted time and store as slewTime
            double inputTime;
            double slewTime;
            if (ValidInputTime(time))
            {
                inputTime = Convert.ToDouble(time);
                if ((inputTime % 1 == 0) && (inputTime > 0))
                    slewTime = inputTime - TRIGGER_SLEW_CORRECTION;
                else if ((inputTime % 1 > (1 - TRIGGER_SLEW_CORRECTION)) && (inputTime % 1 < 1))
                    slewTime = inputTime + (1 - (inputTime % 1)) - TRIGGER_SLEW_CORRECTION;
                else
                    slewTime = inputTime;
            }
            else
                return false; // if the input time is not valid

            // If the number of frames and the filter type is valid, create a new filter setting with the given parameters
            if (ValidNumFrames(frames) && ValidFilter(filterType))
            {
                // Lock the list for adding
                lock (_current_settings_lock)
                {
                    // Build the new filter setting and add it to the current list
                    int newIndex = _filter_settings.Count + 1;
                    _filter_settings.Add(new FilterSetting
                    {
                        FilterType = filterType.ToString(),
                        DisplayTime = slewAdjust ? slewTime : inputTime,
                        UserInputTime = inputTime,
                        SlewAdjustedTime = slewTime,
                        NumExposures = Convert.ToInt16(frames),
                        OrderLocation = newIndex
                    });
                }

                return true; // if add is successful
            }
            return false; // if num frames and filter type are invalid
        }

        /// <summary>
        /// Edits a given filter with the specified settings in the ObservableCollection _FILTER_SETTINGS
        /// </summary>
        /// <param name="toBeChanged">The object in _FILTER_SETTINGS to be changed </param>
        /// <param name="filterType">The new type of the filter</param>
        /// <param name="time">The new exposure time for the filter (in seconds)</param>
        /// <param name="frames">The new number of frames to consecutively capture with these settings</param>
        /// <param name="slewAdjust">True if the times should be adjusted to account for trigger timing slew, false otherwise</param>
        /// <returns>true if the edit occurred, false otherwise</returns>
        public bool Edit(FilterSetting toBeChanged, object filterType, string time, string frames, bool slewAdjust)
        {
            // If the input time is valid, calculate slew adjusted time
            double inputTime;
            double slewTime;
            if (ValidInputTime(time))
            {
                inputTime = Convert.ToDouble(time);
                if ((inputTime % 1 == 0) && (inputTime > 0))
                    slewTime = inputTime - TRIGGER_SLEW_CORRECTION;
                else
                    slewTime = inputTime;
            }
            else
                return false; // if the input time is invalid

            // If num frames and filter type are valid, change the values in this filter setting
            if (ValidNumFrames(frames) && ValidFilter(filterType))
            {
                // Lock the list to perform an edit
                lock (_current_settings_lock)
                {
                    // Update the values in the filter setting
                    toBeChanged.FilterType = filterType.ToString();
                    toBeChanged.DisplayTime = slewAdjust ? slewTime : inputTime;
                    toBeChanged.UserInputTime = inputTime;
                    toBeChanged.SlewAdjustedTime = slewTime;
                    toBeChanged.NumExposures = Convert.ToInt16(frames);
                }

                return true; // if the edit occurred
            }

            return false; // if the frames or filter type were invalid
        }

        /// <summary>
        /// Delete the selected items from the FilterSettingsList
        /// </summary>
        /// <param name="selected">The selected items to delete</param>
        public void DeleteSelected(System.Collections.IList selected)
        {
            // Lock on the current settings list to delete
            lock (_current_settings_lock)
            {
                int numSelected = selected.Count;
                for (int i = 0; i < numSelected; i++)
                {
                    // Remove from the front of the list to avoid indexing errors
                    _filter_settings.Remove(((FilterSetting)selected[0]));
                }

                // Update the index values of each filter setting to reflect the new list
                UpdateLocVals();
            }
        }

        /// <summary>
        /// Update the indices in the list to reflect changes in ordering, additions, or deletions
        /// </summary>
        private void UpdateLocVals()
        {
            // For each filter setting, update the OrderLocation to be the index of the setting + 1.
            for (int i = 0; i < _filter_settings.Count; i++)
            {
                _filter_settings[i].OrderLocation = i + 1;
            }
        }

        #region Validate Inputs

        /// <summary>
        /// Checks if the entered exposure time in the ExposureTime textbox is valid.
        /// Valid includes:
        /// - The time can be converted to a double.
        /// - The time is greater than or equal to 0.
        /// - The input is not NaN
        /// 
        /// If any of these conditions are not met, the user will be informed via a MessageBox.
        /// </summary>
        /// <param name="input">The text entered in the InputTime textbox</param>
        /// <returns>true if the entered value is a valid time, false otherwise</returns>
        private static bool ValidInputTime(string input)
        {
            double UserInputTime;
            try
            {
                UserInputTime = Convert.ToDouble(input);
            }
            catch (FormatException)
            {
                MessageBox.Show("Exposure Time must be a number.\nPlease ensure the entered value is a number zero or greater.", "Doh!");
                return false;
            }
            if (Convert.ToDouble(input) < 0)
            {
                MessageBox.Show("Exposure Time must be 0 seconds or greater.\nPlease ensure the entered value is a number zero or greater.", "Doh!");
                return false;
            }
            if (input == "NaN")
            {
                MessageBox.Show("Nice try.  Please enter a number 0 seconds or greater.", "Doh!");
                return false;
            }
            return true;
        }

        /// <summary>
        /// Checks if the entered number of frames in the NumFrames textbox is valid.
        /// Valid includes:
        /// - The input can be converted to a 16 bit integer
        /// - The input is greater than 0
        /// - The input is not NaN
        /// 
        /// The user is informed if any of the above conditions are not met.
        /// </summary>
        /// <param name="input">The text entered in the NumFrames textbox</param>
        /// <returns>true if the entered value is a valid number, false otherwise</returns>
        private static bool ValidNumFrames(string input)
        {
            double UserNumFrames;
            try
            {
                UserNumFrames = Convert.ToInt16(input);
            }
            catch (FormatException)
            {
                MessageBox.Show("The number of frames must be an integer number.\nPlease ensure the entered value is an integer number greater than zero.", "Doh!");
                return false;
            }
            if (Convert.ToInt16(input) <= 0)
            {
                MessageBox.Show("The number of frames must be greater than zero.\nPlease ensure the entered value is a number greater than zero.", "Doh!");
                return false;
            }
            if (input == "NaN")
            {
                MessageBox.Show("Nice try.  Please enter a number greater than zero.", "Doh!");
                return false;
            }
            return true;
        }

        /// <summary>
        /// Checks that the user has selected a filter in the FilterSelectionBox
        /// </summary>
        /// <param name="f">The combo box object representing the selected choice in the FilterSelectionBox</param>
        /// <returns>false if the object is null, true otherwise</returns>
        private static bool ValidFilter(object f)
        {
            // Ensure user selected a filter
            // If the provided object is null or the empty string, the user has not selected a filter.
            if (f == null || f.ToString() == "")
            {
                MessageBox.Show("You must select a filter.\nPlease ensure you have selected a filter from the drop down menu.", "Doh!");
                return false;
            }
            return true;
        }

        #endregion // Validate Inputs

        #endregion // Modifiers

        #region File IO

        /// <summary>
        /// Writes a string of content to a .dat file
        /// </summary>
        /// <param name="content">The string of information to be written to the file</param>
        /// <param name="filename">The location to save the file.  Can be provided or null.  If null, the user will be prompted.</param>
        public void CurrentSettingsSave(bool adjusted, string filename = null)
        {
            string content = this.GenerateFileContent();

            // Add the adjusted flag
            if (adjusted)
                content = ControlPanel.TRIGGER_ADJUSTED_STRING + "\r\n" + content;
            else
                content = ControlPanel.TRIGGER_UNALTERED_STRING + "\r\n" + content;
            
            try
            {
                // If no filename was given, ask the user to provide one
                if (filename == null)
                {
                    // Configure save file dialog box
                    Microsoft.Win32.SaveFileDialog dlg = new Microsoft.Win32.SaveFileDialog();
                    dlg.FileName = "FilterSettings"; // Default file name
                    dlg.DefaultExt = ".dat"; // Default file extension
                    dlg.Filter = "Filter data files (.dat)|*.dat"; // Filter files by extension

                    Nullable<bool> result = dlg.ShowDialog(); // show the dialog box and store the result

                    // If the result is true, the user provided a location to save the file.
                    if (result == true)
                        filename = dlg.FileName;
                }
                else
                    filename += "_FilterSettings.dat";
                
                // Now, if we've got a filename, save the file.
                if (filename != null)
                {
                    FileStream output = File.Create(filename.ToString());
                    Byte[] info = new UTF8Encoding(true).GetBytes(content);

                    output.Write(info, 0, info.Length);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("There was an error saving your file.  See info here:\n\n" + ex.ToString());
            }
        }

        /// <summary>
        /// Loads a file containing filter and timing information into the current settings list.
        /// </summary>
        /// <returns>A string holding the contents of the selected file.</returns>
        public string CurrentSettingsLoad()
        {
            try
            {
                // Configure open file dialog box
                Microsoft.Win32.OpenFileDialog dlg = new Microsoft.Win32.OpenFileDialog();
                dlg.FileName = "FilterSettings"; // Default file name
                dlg.DefaultExt = ".dat"; // Default file extension
                dlg.Filter = "Filter data files (.dat)|*.dat"; // Filter files by extension

                // Show open file dialog box
                Nullable<bool> result = dlg.ShowDialog();

                // Process open file dialog box results
                if (result == true)
                {
                    // Open document
                    string filename = dlg.FileName;

                    byte[] bytes;

                    using (FileStream fsSource = new FileStream(filename, FileMode.Open, FileAccess.Read))
                    {

                        // Read the source file into a byte array.
                        bytes = new byte[fsSource.Length];
                        int numBytesToRead = (int)fsSource.Length;
                        int numBytesRead = 0;
                        while (numBytesToRead > 0)
                        {
                            // Read may return anything from 0 to numBytesToRead.
                            int n = fsSource.Read(bytes, numBytesRead, numBytesToRead);

                            // Break when the end of the file is reached.
                            if (n == 0)
                                break;

                            numBytesRead += n;
                            numBytesToRead -= n;
                        }
                    }

                    string return_val = System.Text.Encoding.UTF8.GetString(bytes, 0, bytes.Length);

                    return return_val;
                }
            }
            catch (FileNotFoundException e)
            {
                MessageBox.Show("There was an error reading in the file.  See info here:\n\n" + e.Message);
            }
            return null;
        }

        #endregion // File IO
    }
}
