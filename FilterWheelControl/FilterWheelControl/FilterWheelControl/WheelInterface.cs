using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Threading;
using System.Text.RegularExpressions;

using System.IO.Ports;

namespace FilterWheelControl
{
    public class WheelInterface
    {
        #region Static Variables

        public static readonly List<string> _LOADED_FILTERS = new List<string> { "u", "g", "r", "i", "z", "EMPTY", "BLOCK", "BG40" }; // The list of loaded filters in the wheel, ordered clockwise from 0 position.
        public static readonly double _TIME_BETWEEN_ADJACENT_FILTERS = 1.49; // in seconds

        // The time subtracted from all integer-second exposure times, in seconds, when the Adjust Exposure Times for Trigger box is selected
        private static readonly double TRIGGER_SLEW_CORRECTION = 0.005; // seconds

        private static readonly string _PORT_NAME = "COM82"; // This must be set when the filter wheel is attached to the computer.
        private static readonly int _BAUD_RATE = 9600;
        private static readonly string _NEWLINE = "\r\n";

        private static readonly string _MOVE = "mv"; // The base text for the move command.  Must be followed by a number corresponding to wheel position
        private static readonly string _HOME = "hm"; // The base text for the home command
        private static readonly string _INQUIRE = "?"; // The base text for the inquire command
        //private static readonly string _CHECK_STATUS = "CS";
        private static readonly string _SUBMIT = "\r"; // The string to submit a command to the wheel (simply a return)
        //private static readonly string _PROMPT = ">";
        private static readonly int _CW = 9999; // used to signify when the filter wheel is going to rotate clockwise.  Simply a large integer that would not otherwise appear under normal usage.
        private static readonly int _CCW = 8888; // used to signify when the filter wheel is going to rotate counterclockwise.  Simply a large integer that is not otherwise used.

        private static readonly char[] _DELIMITERS = { '=', ' ', '>', '\r', '\n' }; // Delimiters in the return buffer from the filter wheel

        private static volatile Queue<string> _QUEUE; // the instruction queue for the filter wheel
        private static readonly object _connection_lock = new object(); // The object locked on to lock the connection

        public static readonly string _TRANSIT = "Transitioning"; // Filter type value when the wheel is transitioning

        public static readonly long _COMMAND_TIMER_INTERVAL = 100000; // the number of ticks (100ns units) to wait between attempting to send new commands, currently 10ms
        public static readonly long _TIMEOUT_TIMER_INTERVAL = 60000000; // the number of ticks(100ns units) to wait before firing the timeout event, currently 6s

        #endregion // Static Variables

        #region Instance Variables

        private SerialPort _port; // The serial port connection to the filter wheel.  Currently hard-coded as _PORT_NAME
        private SerialDataReceivedEventHandler _data_received; // The event handler for receiving data from the connection
        private SerialErrorReceivedEventHandler _error_received; // The event handler for receiving an error from the connection
        private ControlPanel _panel; // The control panel associated with this wheel interface
        private System.Windows.Threading.DispatcherTimer _send_command_timer; // The timer for sending commands to the filter wheel
        private System.Windows.Threading.DispatcherTimer _timeout_timer; // The timer keeping track of timeout events
        private volatile bool _is_free; // Checks if the connection is free.  Possibly redundant because of locks, but I haven't looked into it yet.
        private volatile string _current_filter; // The filter currently in front of the camera
        public volatile bool _connected; // The connection status with the filter wheel


        #endregion // Instance Variables 

        #region Constructors

        /// <summary>
        /// Opens a connection to the filter wheel via a serial port.
        /// Creates timers for timeout and sending commands.
        /// </summary>
        public WheelInterface(ControlPanel p)
        {
            try
            {
                // Open a connection with the port with the specified settings
                OpenPortConnection();
                
                // Save the control panel object and create the command queue
                this._panel = p;
                _QUEUE = new Queue<string>();

                // Create event handler for DataRecieved and ErrorReceived events.  Assign the error received event handler.
                this._data_received = new SerialDataReceivedEventHandler(_fw_DataReceived);
                this._error_received = new SerialErrorReceivedEventHandler(_fw_ErrorReceived);
                _port.ErrorReceived += _error_received;
                

                // Create timer for queue sending
                _send_command_timer = new System.Windows.Threading.DispatcherTimer();
                _send_command_timer.Tick += new EventHandler(_send_command_Tick);
                _send_command_timer.Interval = new TimeSpan(_COMMAND_TIMER_INTERVAL);

                // Create timer for timeout
                _timeout_timer = new System.Windows.Threading.DispatcherTimer();
                _timeout_timer.Interval = new TimeSpan(_TIMEOUT_TIMER_INTERVAL);

                // Set the _connected value to true and set the ping status on the instrument panel to show a connnected wheel
                _connected = true;
                Application.Current.Dispatcher.BeginInvoke(new Action(() => _panel.PingStatusConnected()));
            }
            catch (Exception e)
            {
                // There was a problem setting up the connection.  Update the ping status on the instrument panel and inform the user.
                Application.Current.Dispatcher.BeginInvoke(new Action(() => _panel.PingStatusDisconnected()));
                ProvideErrorInformation(e.Message);
            }
        }

        /// <summary>
        /// Provides an error message to the user stating that an error occured while attempting to connect to the filter wheel.
        /// </summary>
        /// <param name="s">The message from the exception thrown.</param>
        private void ProvideErrorInformation(string s) 
        {
            MessageBox.Show("There was an error establishing a connection to the filter wheel.  Please see this (possibly helpful) info:\n\n" + s);
        }

        #endregion // Constructors

        #region Port Communications

        /// <summary>
        /// Opens a connection to _PORT_NAME with a Baud Rate of _BAUD_RATE.
        /// Sets the newline character to _NEWLINE and the timeout time to _TIMEOUT.
        /// </summary>
        private void OpenPortConnection() 
        {
            // Open the port connection
            this._port = new SerialPort(_PORT_NAME, _BAUD_RATE, Parity.None, 8, StopBits.One);
            this._port.NewLine = _NEWLINE; // newline character
            _connected = true;
        }

        /// <summary>
        /// Adds a command to the command queue for the filter wheel.
        /// </summary>
        /// <param name="s">The string holding the command to be added.  The string must be formatted correctly.</param>
        private void AddToQueue(string s)
        {
            // Lock the connection
            lock (_connection_lock)
            {
                // Enqueue the command and start the send command timer if necessary
                _QUEUE.Enqueue(s);
                if (!_send_command_timer.IsEnabled)
                {
                    _send_command_timer.Start();
                }
            }
        }

        /// <summary>
        /// Sends a command to the filter wheel on every timer tick.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void _send_command_Tick(object sender,  EventArgs e)
        {
            // If the connection is free and there are commands in the queue, begin processing the next command
            if (_is_free && _QUEUE.Count > 0)
            {
                try
                {
                    // Reserve the connection
                    _is_free = false;

                    // Open the port and hook up the DataReceived event to the handler
                    _port.Open();
                    _port.DataReceived += _data_received;

                    // Get the next command from the queue
                    string command = _QUEUE.Dequeue();

                    // If the next command is a move or home command, process it separately
                    if (command.Contains(_MOVE) || command.Contains(_HOME))
                    {
                        // If the command is a clockwise or counterclockwise move (to position 9999 or 8888), calculate the actual position to move to based on the current filter
                        if (command.Contains(Convert.ToString(_CW)))
                        {
                            int cur = _LOADED_FILTERS.IndexOf(_current_filter);
                            command = cur == 0 ? _MOVE + (_LOADED_FILTERS.Count - 1) + _SUBMIT : _MOVE + (_LOADED_FILTERS.IndexOf(_current_filter) - 1) + _SUBMIT;
                            _timeout_timer.Interval = new TimeSpan(_TIMEOUT_TIMER_INTERVAL / 2L); // Shorten the timeout time interval for quick operations
                        }
                        else if (command.Contains(Convert.ToString(_CCW)))
                        {
                            int cur = _LOADED_FILTERS.IndexOf(_current_filter);
                            command = cur == _LOADED_FILTERS.Count - 1 ? _MOVE + 0 + _SUBMIT : _MOVE + (_LOADED_FILTERS.IndexOf(_current_filter) + 1) + _SUBMIT;
                            _timeout_timer.Interval = new TimeSpan(_TIMEOUT_TIMER_INTERVAL / 2L); // Shorten the timeout time interval for quick operations
                        }

                        // Update the instrument panel to show rotation and set the current filter to _TRANSIT
                        Application.Current.Dispatcher.BeginInvoke(new Action(_panel.UpdateFWInstrumentRotate));
                        _current_filter = _TRANSIT;
                    }

                    // Write the command to the port and start the timeout timer
                    _port.WriteLine(command);
                    _timeout_timer.Tick += _timeout_timer_Tick;
                    _timeout_timer.Start();
                }
                catch (Exception ex)
                {
                    // There was an error opening a connection to the wheel and writing a command.
                    // Stop the timer and clear the queue.  Show we are not connected to the user and display a message.
                    _send_command_timer.Stop();
                    _QUEUE.Clear();
                    _connected = false;
                    Application.Current.Dispatcher.BeginInvoke(new Action(() => _panel.PingStatusDisconnected()));
                    MessageBox.Show("The connection to the filter wheel has been lost.  Please attempt to restore a connection.\n\nNo more filter movements will occur.  You may want to halt data acquisition until the problem is resolved.\n\nHere is some more information:\n" + ex.Message);
                }
            }
            else if (_QUEUE.Count == 0)
            {
                // if there is nothing in the queue, stop the command timer
                _send_command_timer.Stop();
            }
        }

        /// <summary>
        /// Processes the output from the commands sent to the filter wheel.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void _fw_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            // Lock the connection to the wheel
            lock (_connection_lock)
            {
                // Disconnect the DataReceived event from this handler.  It will be reconnected when a new command is sent to the filter wheel.
                _port.DataReceived -= _data_received;
                
                // Stop the timeout timer.  We have received the data in due time.
                // Reset the timeout timer interval to the _TIMEOUT_TIMER_INTERVAL value
                _timeout_timer.Stop();
                _timeout_timer.Tick -= _timeout_timer_Tick;
                _timeout_timer.Interval = new TimeSpan(_TIMEOUT_TIMER_INTERVAL);
                
                // Read the existing port output buffer
                string output = _port.ReadExisting();

                // Handle the inquiry output
                if (output.Contains("W1 = "))
                    ProcessInquiry(output);
                
                // If the port is open, close it and free it for more commands.
                if(_port.IsOpen)
                    _port.Close();
                _is_free = true;
            }
        }

        /// <summary>
        /// Stops command processing, alerts the user, and clears the queue if an error is received.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void _fw_ErrorReceived(object sender, SerialErrorReceivedEventArgs e)
        {
            // Stop the command and timeout timers.  Let the user know the wheel is disconnected.
            _send_command_timer.Stop();
            _timeout_timer.Stop();
            Application.Current.Dispatcher.BeginInvoke(new Action(() => _panel.PingStatusDisconnected()));
            
            ProvideErrorInformation("An unknown connection error with the filter wheel was recieved during normal operation.  Please double check the connection before continuing.");
            
            // If the port is open, close it.  Free the connection and clear the queue.
            if (_port.IsOpen)
            {
                _port.Close();
            }
            _is_free = true;
            _QUEUE.Clear();
        }

        /// <summary>
        /// Attempts to open and close the port to determine if the connection is good.
        /// </summary>
        /// <returns>0 if a port is already open or is opened and then closed successfully, 1 otherwise.</returns>
        public int PingConnection()
        {
            // Lock the connection
            lock (_connection_lock)
            {
                // Store the previous connected status
                bool last_status = _connected;
                int result;
                
                // If the port is open, assume the connection is good.  Let the user know.
                if (_port.IsOpen)
                {
                    _connected = true;
                    Application.Current.Dispatcher.BeginInvoke(new Action(() => _panel.PingStatusConnected()));
                    result = 0;
                }
                else
                {
                    // Otherwise, try to open and close a port to test the connection
                    try
                    {
                        // Reserve the connection
                        _is_free = false;
                        
                        // Open the port
                        _port.Open();

                        // If the port is open now, we've succeeded!  Close the port and update the instrument panel to show we are connected.
                        if (_port.IsOpen)
                        {
                            _port.Close();
                            _is_free = true;
                            _connected = true;
                            Application.Current.Dispatcher.BeginInvoke(new Action(() => _panel.PingStatusConnected()));
                            result = 0;
                        }
                        else
                        {
                            // Something horrible has happened.  Free the connection, and let the user know we are disconnected.
                            _is_free = true;
                            _connected = false;
                            Application.Current.Dispatcher.BeginInvoke(new Action(() => _panel.PingStatusDisconnected()));
                            result = 1;
                        }
                    }
                    catch (Exception e)
                    {
                        // If the exception is because the port was already open, then things are okay.  The connection is good.
                        if (e.Message == "Port is already open.")
                        {
                            _connected = true;
                            Application.Current.Dispatcher.BeginInvoke(new Action(() => _panel.PingStatusConnected()));
                            result = 0;
                        }
                        else
                        {
                            // The connection is not good.  Let the user know
                            _connected = false;
                            Application.Current.Dispatcher.BeginInvoke(new Action(() => _panel.PingStatusDisconnected()));
                            result = 1;
                        }
                    }
                }

                // If we were previously disconnected, but now we are connected, home the wheel for safety.
                if (last_status == false && _connected == true)
                    Home();
                
                return result;
            }
        }

        /// <summary>
        /// Handles the case of a timeout with the filter wheel.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void _timeout_timer_Tick(object sender, EventArgs e)
        {
            // Disconnected the tick event from this handler and stop the timeout and command timers
            _timeout_timer.Tick -= _timeout_timer_Tick;
            _send_command_timer.Stop();
            _timeout_timer.Stop();
            
            // Set _connected to false and clear all commands from the queue
            _connected = false;
            _QUEUE.Clear();

            // Disconnect the DataReceived event from the handler
            _port.DataReceived -= _fw_DataReceived;

            // Try closing the port if it's open.
            try
            {
                if (_port.IsOpen)
                {
                    _port.Close();
                    _is_free = true;
                }
            }
            catch (Exception)
            {
                // The filter wheel has become disconnected.  Inform of disconnect and return.
                InformDisconnect();
                return;
            }

            // If we don't catch an exception while trying to close the port, then the error must be a timeout (i.e. the connection is good but we aren't receiving data from the wheel).
            // Show we are disconnected and inform the user of this situation.
            Application.Current.Dispatcher.BeginInvoke(new Action(() => _panel.PingStatusDisconnected()));
            MessageBox.Show("Commands to the filter wheel have timed out.\nPlease check all hardware connections, including filter wheel motor power, and try again.", "Timeout Error");
        }

        /// <summary>
        /// Resets the connection to the port.
        /// Clears all commands from the Queue.
        /// </summary>
        public void ResetConnection()
        {
            try
            {
                // Stop all current command processing, clear the queue, and close the port
                _send_command_timer.Stop();
                _QUEUE.Clear();
                if (_port.IsOpen)
                    _port.Close();
                _is_free = true;
                _connected = false;

                // Dispose the current serial port connection
                _port.Dispose();
                _port = null;

                // Attempt to reconnect to the filter wheel port with a new connection
                OpenPortConnection();
                Application.Current.Dispatcher.BeginInvoke(new Action(() => _panel.PingStatusConnected()));
            }
            catch (Exception e)
            {
                // We did not succeed in resetting the connection.  Inform the user.
                Application.Current.Dispatcher.BeginInvoke(new Action(() => _panel.PingStatusDisconnected()));
                MessageBox.Show("An error occurred while attempting to reset the connection.  Here is some more information:\n\n" + e.Message);
            }
        }

        /// <summary>
        /// Closes all connections to the filter wheel.
        /// Discards both buffers.
        /// </summary>
        public void ShutDown()
        {
            _send_command_timer.Stop();
            _timeout_timer.Stop();
            _port.ErrorReceived -= _error_received;
            _QUEUE.Clear();
            if (_port.IsOpen)
                _port.Close();
            _is_free = true;
            _port.DiscardInBuffer();
            _port.DiscardOutBuffer();
            _port.Dispose();
        }

        /// <summary>
        /// Stops the queue command timer and informs the user of a disconnect.
        /// </summary>
        public void InformDisconnect()
        {
            _send_command_timer.Stop();
            Application.Current.Dispatcher.BeginInvoke(new Action(() => _panel.PingStatusDisconnected()));
            MessageBox.Show("The filter wheel is not connected.  Please Ping the connection and try again.\n\nIf the problem persists, please check all hardware components.");
        }

        #endregion // Port Communications

        #region Output Processing

        /// <summary>
        /// Processes the result of an inquiry command.
        /// Updates the Filter Wheel instrument on the instrument panel.
        /// Does nothing if the result is unknown (?).
        /// </summary>
        /// <param name="inq">A string holding the output of the inquire command.</param>
        private void ProcessInquiry(string inq)
        {
            // Split the inquire response into important values
            string[] values = inq.Split(_DELIMITERS, StringSplitOptions.RemoveEmptyEntries);

            // If values[1] is a ?, the wheel doesn't know where it is.  Give up.
            if (values[1] == "?")
            {
                return;
            }
            else
            {
                // We know where we are.
                // Convert the wheel position (stored in values[1]) to an int.  
                // Build an ordered list of filters starting with the current position, and update the instrument panel to show this new ordering.
                int cur = Convert.ToInt16(values[1]);
                Application.Current.Dispatcher.Invoke(new Action(() => _panel.UpdateFWInstrumentOrder(BuildOrderedSet(cur))));
                
                // Update _current_filter
                _current_filter = _LOADED_FILTERS[cur];
            }
        }

        /// <summary>
        /// Provides the filters currently in the wheel, ordered with the 0th element being the filter in the prime position, moving clockwise.
        /// Assumes the _LOADED_FILTERS list is ordered moving clockwise
        /// </summary>
        /// <returns>A list of strings holding the filter types.</returns>
        private List<string> BuildOrderedSet(int cur)
        {
            List<string> ordered = new List<string>();
            int i = cur;
            
            // Add all the filters to the list from the current to the end of the list.
            while (i < _LOADED_FILTERS.Count)
            {
                ordered.Add(_LOADED_FILTERS[i]);
                i++;
            }

            i = 0;

            // Add all the filters to the list from the start to current.
            while (i < cur)
            {
                ordered.Add(_LOADED_FILTERS[i]);
                i++;
            }

            return ordered;
        }

        #endregion // Output Processing

        #region Input Processing

        /// <summary>
        /// Rotates the filter wheel to the specified filter.  
        /// Does nothing if the input is not a filter type currently in the filter wheel.
        /// </summary>
        /// <param name="type">A string representing the filter type to rotate to.  Must be included in _LOADED_FILTERS</param>
        public void RotateToFilter(object type)
        {
            // Scrub input for weird non-strings
            string ftype;
            try
            {
                ftype = (string)type;
            }
            catch (FormatException)
            {
                return;
            }

            // If type is in _LOADED_FILTERS, rotate to it.
            ftype = (string)type;
            if (_LOADED_FILTERS.Contains(ftype))
            {
                int loc = _LOADED_FILTERS.IndexOf(ftype);
                MoveTo(loc); 
            }
        }

        /// <summary>
        /// Adds the mv command to the filter wheel command queue, followed by the inquire command.
        /// </summary>
        /// <param name="loc">The location to move the filter wheel to.</param>
        private void MoveTo(int loc)
        {
            // If we are connected, add the move command to the queue, followed by an inquire command to update the filter wheel instrument and _current_filter variables
            if (_connected)
            {
                AddToQueue(loc + _MOVE + _SUBMIT);
                Inquire();
            }
            else
            {
                // Let the user know we are disconnected
                InformDisconnect();
            }
        }

        /// <summary>
        /// Adds the hm command to the filter wheel command queue, followed by the inquire command.
        /// </summary>
        public void Home()
        {
            // If we are connected, add the home command to the queue, followed by an inquire command to update the filter wheel instrument and _current_filter variables
            if (_connected)
            {
                AddToQueue(_HOME + _SUBMIT);
                Inquire();
            }
            else
            {
                // Let the user know we are disconnected
                InformDisconnect();
            }
        }

        /// <summary>
        /// Clears the queue and adds the hm command to the filter wheel command queue, followed by the inquire command.
        /// </summary>
        public void EmergencyHome()
        {
            // If we are connected, clear the queue and add the home command to the queue. 
            // Followed by an inquire command to update the filter wheel instrument and _current_filter variables
            if (_connected)
            {
                _QUEUE.Clear();
                AddToQueue(_HOME + _SUBMIT);
                Inquire();
            }
            else
            {
                // Let the user know we've disconnected
                InformDisconnect();
            }
        }

        /// <summary>
        /// Add the ? command to the filter wheel command queue.
        /// </summary>
        public void Inquire()
        {
            // If we are connected, add the inquire command to the queue
            if (_connected)
            {
                AddToQueue(_INQUIRE + _SUBMIT);
            }
            else
            {
                // Let the user know we're disconnected
                InformDisconnect();
            }
        }

        /// <summary>
        /// Sets up the system for a single movement of the wheel in the clockwise direction w.r.t. the camera.
        /// </summary>
        public void Clockwise()
        {
            // If we are connected, first add the inquire command to the wheel.
            // Then move clockwise based on the current filter returned by the inquire command.
            if (_connected)
            {
                Inquire();
                MoveTo(_CW);
            }
            else
            {
                // Let the user know we're disconnected.
                InformDisconnect();
            }
        }

        /// <summary>
        /// Sets up the system for a single movement of the wheel in the counterclockwise direction w.r.t. the camera.
        /// </summary>
        public void CounterClockwise()
        {
            // If we are connected, first add the inquire command to the wheel.
            // Then move counterclockwise based on the current filter returned by the inquire command.
            if (_connected)
            {
                Inquire();
                MoveTo(_CCW);
            }
            else
            {
                // Let the user know we're disconnected
                InformDisconnect();
            }
        }

        #endregion // Input Processing

        #region Accessors

        /// <summary>
        /// Returns the time, in seconds, between the two provided filters, assuming a constant time between adjacent filters of _TIME_BETWEEN_ADJACENT_FILTERS
        /// </summary>
        /// <param name="f1">One of the filters to calculate time between.</param>
        /// <param name="f2">The other filter to calculate time between.</param>
        /// <returns>The time, in seconds, between the two provided filters.</returns>
        public static double TimeBetweenFilters(string f1, string f2)
        {
            int stop = _LOADED_FILTERS.Count;

            // Deterime the number of positions betwen the two filters in the clockwise direction.
            int pos1 = 0;
            while (pos1 < stop && (_LOADED_FILTERS[pos1] != f1 && _LOADED_FILTERS[pos1] != f2))
            {
                pos1++;
            }

            int pos2 = pos1 + 1;
            while (pos2 < stop && (_LOADED_FILTERS[pos2] != f1 && _LOADED_FILTERS[pos2] != f2))
            {
                pos2++;
            }
            
            // Calculate the true amount of time it takes to rotate between two filters by taking
            // the minimum of the clockwise and counter-clockwise rotation times
            double true_rotation_time = Math.Min((pos2 - pos1), stop - (pos2 - pos1)) * _TIME_BETWEEN_ADJACENT_FILTERS;

            // Round the calculated exposure time up to the nearest integer number of seconds
            double rounded_rotation_time = Math.Ceiling(true_rotation_time);

            // Return the calculated rotation time corrected for the trigger-wait adjustment time.
            return rounded_rotation_time - TRIGGER_SLEW_CORRECTION;
        }

        /// <summary>
        /// Access the value of _TIME_BETWEEN_ADJACENT_FILTERS
        /// </summary>
        /// <returns>The value of _TIME_BETWEEN_ADJACENT_FILTERS</returns>
        public double TimeBetweenAdjacentFilters()
        {
            return _TIME_BETWEEN_ADJACENT_FILTERS;
        }

        /// <summary>
        /// Access the number of filters in the wheel.
        /// </summary>
        /// <returns>The number of filters in the _LOADED_FILTERS list (8).</returns>
        public int NumFilters()
        {
            return _LOADED_FILTERS.Count;
        }

        #endregion // Accessors

        #region Modifiers



        #endregion // Modifiers

    }
}
