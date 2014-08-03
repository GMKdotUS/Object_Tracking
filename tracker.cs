// Lego Pan Tilt Camera and Objects Tracking
//
// Copyright © Andrew Kirillov, 2008
// andrew.kirillov@gmail.com
//

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using System.Threading;

using AForge;
using AForge.Robotics.Lego;
using AForge.Video;
using AForge.Video.DirectShow;
using AForge.Imaging;
using AForge.Imaging.Filters;

namespace LegoCamera
{
    public partial class MainForm : Form
    {
        private FilterInfoCollection videoDevices;
        private NXTBrick nxt = new NXTBrick( );

        // background thread and objects used for synchronization
        private Thread thread = null;
        private AutoResetEvent needToDriveEvent = null;
        private bool needToExit = false;

        // motors' power to set
        private float panMotorPower  = 0;
        private float tiltMotorPower = 0;

        private float lastPanMotorPower  = 0;
        private float lastTiltMotorPower = 0;

        // image processing stuff
        ColorFiltering colorFilter = new ColorFiltering( );
        GrayscaleBT709 grayscaleFilter = new GrayscaleBT709( );
        BlobCounter blobCounter = new BlobCounter( );

        // Form contructor
        public MainForm( )
        {
            InitializeComponent( );

            // configure blob counter
            blobCounter.MinWidth  = 25;
            blobCounter.MinHeight = 25;
            blobCounter.FilterBlobs = true;
            blobCounter.ObjectsOrder = ObjectsOrder.Size;

            // collect cameras list
            try
            {
                // enumerate video devices
                videoDevices = new FilterInfoCollection( FilterCategory.VideoInputDevice );

                if ( videoDevices.Count == 0 )
                    throw new ApplicationException( );

                // add all devices to combo
                foreach ( FilterInfo device in videoDevices )
                {
                    camerasCombo.Items.Add( device.Name );
                }

                camerasCombo.SelectedIndex = 0;
            }
            catch ( ApplicationException )
            {
                camerasCombo.Items.Add( "No local capture devices" );
                videoDevices = null;
            }

            // enable controls, which allow to connect (disconnected state yet)
            EnableConnectionControls( false );

            predefinedColorsCombo.SelectedIndex = 0;
        }

        // Application form is going to close
        private void MainForm_FormClosing( object sender, FormClosingEventArgs e )
        {
            Disconnect( );
        }

        // On "Connect" button click
        private void connectButton_Click( object sender, EventArgs e )
        {
            if ( Connect( ) )
            {
                EnableConnectionControls( true );
            }
            else
            {
                MessageBox.Show( "Failed connecting to NXT brick", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error );
            }
        }

        // On "Disconnect" button click
        private void disconnectButton_Click( object sender, EventArgs e )
        {
            Disconnect( );
            EnableConnectionControls( false );
        }

        // Enable/disable connection controls
        private void EnableConnectionControls( bool enable )
        {
            nxtComPortBox.Enabled = !enable;
            camerasCombo.Enabled = ( ( !enable ) && ( videoDevices != null ) );
            connectButton.Enabled = ( ( !enable ) && ( videoDevices != null ) );

            disconnectButton.Enabled = enable;
            panController.Enabled = enable;
            tiltController.Enabled = enable;
        }

        // Predefined detection color has changed
        private void predefinedColorsCombo_SelectedIndexChanged( object sender, EventArgs e )
        {
            bool enableCustomConfiguration = ( predefinedColorsCombo.SelectedIndex == predefinedColorsCombo.Items.Count - 1 );

            redMinUpDown.Enabled   = enableCustomConfiguration;
            redMaxUpDown.Enabled   = enableCustomConfiguration;
            greenMinUpDown.Enabled = enableCustomConfiguration;
            greenMaxUpDown.Enabled = enableCustomConfiguration;
            blueMinUpDown.Enabled  = enableCustomConfiguration;
            blueMaxUpDown.Enabled  = enableCustomConfiguration;

            switch ( predefinedColorsCombo.SelectedIndex )
            {
                case 0: // red
                    redMinUpDown.Value   = 140;
                    redMaxUpDown.Value   = 255;
                    greenMinUpDown.Value = 0;
                    greenMaxUpDown.Value = 100;
                    blueMinUpDown.Value  = 0;
                    blueMaxUpDown.Value  = 100;
                    break;

                case 1: // blue
                    redMinUpDown.Value   = 0;
                    redMaxUpDown.Value   = 100;
                    greenMinUpDown.Value = 0;
                    greenMaxUpDown.Value = 100;
                    blueMinUpDown.Value  = 100;
                    blueMaxUpDown.Value  = 255;
                    break;

                default: // custom settings
                    redMinUpDown.Value   = 0;
                    redMaxUpDown.Value   = 255;
                    greenMinUpDown.Value = 0;
                    greenMaxUpDown.Value = 255;
                    blueMinUpDown.Value  = 0;
                    blueMaxUpDown.Value  = 255;
                    break;
            }
        }

        // Minimum red value has changed
        private void redMinUpDown_ValueChanged( object sender, EventArgs e )
        {
            colorFilter.Red = new IntRange(
                Convert.ToInt32( redMinUpDown.Value ), Convert.ToInt32( redMaxUpDown.Value ) );
        }

        // Maximum red value has changed
        private void redMaxUpDown_ValueChanged( object sender, EventArgs e )
        {
            colorFilter.Red = new IntRange(
                Convert.ToInt32( redMinUpDown.Value ), Convert.ToInt32( redMaxUpDown.Value ) );
        }

        // Minimum green value has changed
        private void greenMinUpDown_ValueChanged( object sender, EventArgs e )
        {
            colorFilter.Green = new IntRange(
                Convert.ToInt32( greenMinUpDown.Value ), Convert.ToInt32( greenMaxUpDown.Value ) );
        }

        // Maximum green value has changed
        private void greenMaxUpDown_ValueChanged( object sender, EventArgs e )
        {
            colorFilter.Green = new IntRange(
                Convert.ToInt32( greenMinUpDown.Value ), Convert.ToInt32( greenMaxUpDown.Value ) );
        }

        // Minimum blue value has changed
        private void blueMinUpDown_ValueChanged( object sender, EventArgs e )
        {
            colorFilter.Blue = new IntRange(
                Convert.ToInt32( blueMinUpDown.Value ), Convert.ToInt32( blueMaxUpDown.Value ) );
        }

        // Maximum blue value has changed
        private void blueMaxUpDown_ValueChanged( object sender, EventArgs e )
        {
            colorFilter.Blue = new IntRange(
                Convert.ToInt32( blueMinUpDown.Value ), Convert.ToInt32( blueMaxUpDown.Value ) );
        }

        // Connect to NXT brick and camera
        private bool Connect( )
        {
            // close previois connection if any
            Disconnect( );

            // connect to NXT brick
            if ( !nxt.Connect( nxtComPortBox.Text) )
            {
                // failed to connect
                return false;
            }
            // play something on connection
            nxt.PlayTone( 300, 300 );

            // connect to camera
            VideoCaptureDevice videoSource = new VideoCaptureDevice( videoDevices[camerasCombo.SelectedIndex].MonikerString );
            videoSource.DesiredFrameSize = new Size( 320, 240);
            videoSource.DesiredFrameRate = 15;

            videoSourcePlayer.VideoSource = videoSource;
            videoSourcePlayer.Start( );

            // create event used to signal thread about updates to power state
            needToDriveEvent = new AutoResetEvent( false );
            needToExit = false;

            // create background thread wich drives Lego
            thread = new Thread( new ThreadStart( WorkerThread ) );
            thread.Start( );

            return true;
        }

        // Disconnect from NXT and camera
        private void Disconnect( )
        {
            if ( thread != null )
            {
                // stop background thread
                needToExit = true;
                needToDriveEvent.Set( );
                thread.Join( );

                needToDriveEvent.Close( );
                needToDriveEvent = null;
                thread = null;
            }

            // disconnect from NXT
            nxt.Disconnect( );

            // stop camera
            videoSourcePlayer.SignalToStop( );
            videoSourcePlayer.WaitForStop( );
        }

        // Pan controller's position has changed
        private void panController_PositionChanged( float position )
        {
            panMotorPower = -position;
            needToDriveEvent.Set( );
        }

        // Tilt controller's position has changed
        private void tiltController_PositionChanged( float position )
        {
            tiltMotorPower = -position;
            needToDriveEvent.Set( );
        }

        // Worker thread which is used to set motors' power
        private void WorkerThread( )
        {
            float newPanMotorPower  = 0;
            float newTiltMotorPower = 0;

            while ( true )
            {
                // wait for events
                needToDriveEvent.WaitOne( );

                // should thread exit?
                if ( needToExit )
                {
                    SetMotorPowers( 0, 0 );
                    break;
                }

                lock ( this )
                {
                    newPanMotorPower  = panMotorPower;
                    newTiltMotorPower = tiltMotorPower;
                }

                SetMotorPowers( newPanMotorPower, newTiltMotorPower );
            }
        }

        // Set pan and tilt motors' power
        private void SetMotorPowers( float panMotorPower, float tiltMotorPower )
        {
            // control pan motor
            if ( panMotorPower != lastPanMotorPower )
            {
                lastPanMotorPower = panMotorPower;

                int power = (int) ( 5 * panMotorPower + 55 * Math.Sign( panMotorPower ) );

                NXTBrick.MotorState motorsState = new NXTBrick.MotorState( );
                // check if we need to stop
                if ( power == 0 )
                {
                    motorsState.Mode       = NXTBrick.MotorMode.None;
                    motorsState.RunState   = NXTBrick.MotorRunState.Idle;
                }
                else
                {
                    motorsState.Mode       = NXTBrick.MotorMode.On;
                    motorsState.RunState   = NXTBrick.MotorRunState.Running;
                    motorsState.TachoLimit = 0;
                    motorsState.Power      = power;
                    motorsState.TurnRatio  = 80;
                }

                nxt.SetMotorState( NXTBrick.Motor.A, motorsState );
            }

            // controls tilt motor
            if ( tiltMotorPower != lastTiltMotorPower )
            {
                lastTiltMotorPower = tiltMotorPower;

                int power = (int) ( 5 * tiltMotorPower + 55 * Math.Sign( tiltMotorPower ) );

                NXTBrick.MotorState motorsState = new NXTBrick.MotorState( );
                // check if we need to stop
                if ( power == 0 )
                {
                    motorsState.Mode      = NXTBrick.MotorMode.None;
                    motorsState.RunState  = NXTBrick.MotorRunState.Idle;
                }
                else
                {
                    motorsState.Mode       = NXTBrick.MotorMode.On;
                    motorsState.RunState   = NXTBrick.MotorRunState.Running;
                    motorsState.TachoLimit = 0;
                    motorsState.Power      = power;
                    motorsState.TurnRatio  = 80;
                }

                nxt.SetMotorState( NXTBrick.Motor.B, motorsState );
            }
        }

        // New video frame has arrived
        private void videoSourcePlayer_NewFrame( object sender, ref Bitmap image )
        {
            if ( detectionCheck.Checked )
            {
                bool showOnlyObjects = onlyObjectsCheck.Checked;

                Bitmap objectsImage = null;

                // color filtering
                if ( showOnlyObjects )
                {
                    objectsImage = image;
                    colorFilter.ApplyInPlace( image );
                }
                else
                {
                    objectsImage = colorFilter.Apply( image );
                }

                // lock image for further processing
                BitmapData objectsData = objectsImage.LockBits( new Rectangle( 0, 0, image.Width, image.Height ),
                    ImageLockMode.ReadOnly, image.PixelFormat );

                // grayscaling
                UnmanagedImage grayImage = grayscaleFilter.Apply( new UnmanagedImage( objectsData ) );

                // unlock image
                objectsImage.UnlockBits( objectsData );

                // locate blobs 
                blobCounter.ProcessImage( grayImage );
                Rectangle[] rects = blobCounter.GetObjectsRectangles( );

                if ( rects.Length > 0 )
                {
                    Rectangle objectRect = rects[0];

                    // draw rectangle around derected object
                    Graphics g = Graphics.FromImage( image );

                    using ( Pen pen = new Pen( Color.FromArgb( 160, 255, 160 ), 3 ) )
                    {
                        g.DrawRectangle( pen, objectRect );
                    }

                    g.Dispose( );

                    if ( trackingCheck.Checked )
                    {
                        int objectX = objectRect.X + objectRect.Width / 2 - image.Width / 2;
                        int objectY = image.Height / 2 - ( objectRect.Y + objectRect.Height / 2 );

                        panMotorPower  = (float) -( ( ( Math.Abs( objectX ) > 30 ) ? 0.3 : 0.0 ) * Math.Sign( objectX ) );
                        tiltMotorPower = (float)  ( ( ( Math.Abs( objectY ) > 30 ) ? 0.5 : 0.0 ) * Math.Sign( objectY ) );
                    }
                    else
                    {
                        panMotorPower  = 0;
                        tiltMotorPower = 0;
                    }
                }
                else
                {
                    if ( trackingCheck.Checked )
                    {
                        panMotorPower  = 0;
                        tiltMotorPower = 0;
                    }
                }

                // drive camera's motors if required
                needToDriveEvent.Set( );

                // free temporary image
                if ( !showOnlyObjects )
                {
                    objectsImage.Dispose( );
                }
                grayImage.Dispose( );
            }
        }
    }
}
