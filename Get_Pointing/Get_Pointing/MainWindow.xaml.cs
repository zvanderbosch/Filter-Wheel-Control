using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace Get_Pointing
{ 
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        #region Static Variables

        private static readonly string url_point = 
            "http" + "://secure-web.cisco.com/1L5SJN4LcPRECSxGPS1pPjSVnqSrMf3tDXUv5NKVY82hj" +
            "UAiGkjnISu9WfmjpzkfdVNOwco3ljQd0dsUyGooaJaQtf_rlRoQTy4SyI3wURWNYEyotBcDjcUp" +
            "JdlTLOT-2iZ4IO_NzvvI_77gIsXEglJvXDKfacLTxxGIHFoNA2TT-X6a0LvMcGoEVLCYVjCwOUU" +
            "ihj1x99c9mxHhiT_mjVcoU4BrzyOdbVxQblY_7LUyyOzAwssnHV7xV3uoQTflS_CfWIh7zyanCg" +
            "RfIpU2P1JgHC2rLM5EdENdn9PF151kXGVYRfm7ddMP5qSn_ExZGQwF2-CDJI1V3XYAQzMWdmbg_y" +
            "BlC8OliNpbU7Eas9j7yVPahRPCJAtqpfROgYoZHkwuZ1vyZbWJF2b5rqRkMA2iTyJWI0_opZWup5" +
            "LRJ26bImW62ajmqmR3BHrNkqSPeOcgaxzTx1mWoL6dsD1FUaorpjSHZB9ijT32uWDxkvQYY91fz8" +
            "plcwEXHeu6PxkIYILVDKfNZUsAw3GRcNhP_KLfitj56RyuEpv4dEF0GVwaTUIhcEvlwAObsvXHNj" +
            "c8ex_UcUpEvIfCGQSsy7lT-9g/http%3A%2F%2F198.214.229.56%3A22401%2Firaf";

        private static readonly string url_focus =
            "http" + "://secure-web.cisco.com/1cM57TzpnkHjofMQKiP0vyH4OMpWfk3RHHJmKQuD-B74" +
            "wqkPDkaiO2hwpiiZmCqbxkRzaZbP-Y6Y0n3bT5T_54mLDWpZBOfeH8ODHzdJVelraxj4GUwPGXPzn" +
            "ia1lyBHP-2stbj0wTDZWn_EMvEjMnGPGfaXmNmTEzUJd9qa9YnZKZtutRbUjNbzBy3nLpIPi9HnUz" +
            "m0A3iShJixevZLq6CN9CMN4Fj3PsA-CpPSTEgHTFptU2muXJAZqI1NUynZtABBX-Z7nXbNXcVs--Ka" +
            "BfDjfSrqySznHAF5hvhJIKLUmRIge5cYg6VHUHyrMk9qERfvbf6yagUsuYiUna3dbRVWNF7kuz7T7x" +
            "a7_uGFWqD2LA-n1XpYhWWwbn-JY1HB05FFZ2_zjODCeEH1GH3adLFx_tD2tSTOGrFUdBFmJON-zCA" +
            "DRx-F5NsRojQwBifZLGxpy6tM-rUpgmzwOSph7GkY0rfG3G2TgDPiyznUt3t_Kd8IpqXr20dkqMY0" +
            "mw9gbbknL2pM3DzV9o4EAmYJ_6eVr-f-zrRLOOCIUl2obeS4HX07yWOHa7M047KBYUf5YPklBfsO5" +
            "luJAkmfMkIG5aw/http%3A%2F%2F198.214.229.56%3A22401%2Ffindfocus";
            
        #endregion 

        public MainWindow()
        {
            InitializeComponent();
        }

        // Actions for when the Display button is clicked
        private void Click_Show_Info(object sender, RoutedEventArgs e)
        {
            //Access the URLs
            HttpWebRequest   request_point = (HttpWebRequest)WebRequest.Create(url_point);
            HttpWebRequest   request_focus = (HttpWebRequest)WebRequest.Create(url_focus);
            //Ask for a response from the URLs
            HttpWebResponse response_point = (HttpWebResponse)request_point.GetResponse();
            HttpWebResponse response_focus = (HttpWebResponse)request_focus.GetResponse();
            //Read the response
            TextReader read_point = new StreamReader(response_point.GetResponseStream());
            TextReader read_focus = new StreamReader(response_focus.GetResponseStream());
            //Generate a string from the response
            string point = read_point.ReadToEnd();
            string focus = read_focus.ReadToEnd();
            //Close the connections to the URLs
            response_point.Close();
            response_focus.Close();

            // Split the URL string into individual chunks of information
            // Formatting is a bit weird, ask John Kuehne for his format
            string[] point_split = point.Split(' ','\n','\r');

            foreach (string s in point_split)
            {
                All_Info_box.Text += s + '\n';
            }

            // Assign values to each text display box
            Unknown1_box.Text   = point_split[2] + ' ' + point_split[4] + ' ' + point_split[7];
            UT_Date_box.Text    = point_split[8] + '-' + point_split[9] + '-' + point_split[10];
            UT_Time_box.Text    = point_split[13];
            RA_box.Text         = point_split[15];
            DEC_box.Text        = point_split[22];
            Focus_box.Text      = focus;
            Zenith_Distance_box.Text = point_split[23];
            Azimuth_box.Text    = point_split[24];
            Hour_Angle_box.Text = point_split[20];
            LST_box.Text        = point_split[18];
        }


        private void Click_Clear_Info(object sender, RoutedEventArgs e)
        {
            Unknown1_box.Text = String.Empty;
            UT_Date_box.Text = String.Empty;
            UT_Time_box.Text = String.Empty;
            RA_box.Text = String.Empty;
            DEC_box.Text = String.Empty;
            Focus_box.Text = String.Empty;
            Zenith_Distance_box.Text = String.Empty;
            Azimuth_box.Text = String.Empty;
            Hour_Angle_box.Text = String.Empty;
            LST_box.Text = String.Empty;
            All_Info_box.Text = String.Empty;
            
        }
    }
}
