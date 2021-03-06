﻿#region license
// Copyright (C) 2020 ClassicUO Development Community on Github
// 
// This project is an alternative client for the game Ultima Online.
// The goal of this is to develop a lightweight client considering
// new technologies.
// 
//  This program is free software: you can redistribute it and/or modify
//  it under the terms of the GNU General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  This program is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//  GNU General Public License for more details.
// 
//  You should have received a copy of the GNU General Public License
//  along with this program.  If not, see <https://www.gnu.org/licenses/>.
#endregion


using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;

using ClassicUO.Configuration;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.Managers;
using ClassicUO.Game.Scenes;
using ClassicUO.Game.UI.Controls;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.Input;
using ClassicUO.IO.Resources;
using ClassicUO.Network;
using ClassicUO.Renderer;
using ClassicUO.Utility;
using ClassicUO.Utility.Logging;

using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

using SDL2;
using static SDL2.SDL;

namespace ClassicUO
{
    class GameController : Microsoft.Xna.Framework.Game
    {
        private Scene _scene;
        private bool _dragStarted;
        private bool _ignoreNextTextInput;
        private readonly GraphicsDeviceManager _graphicDeviceManager;
        private UltimaBatcher2D _uoSpriteBatch;
        private readonly float[] _intervalFixedUpdate = new float[2];
        private double _statisticsTimer;
        private RenderTarget2D _buffer;
        private double _totalElapsed, _currentFpsTime;
        private uint _totalFrames;
        private Vector3 _hueVector;

        public event Action sceneChanged;
        public bool MouseOverGui;

        public GameController()
        {
            _graphicDeviceManager = new GraphicsDeviceManager(this);
        }

        public Scene Scene => _scene;
        public UltimaBatcher2D Batcher => _uoSpriteBatch;
        public readonly uint[] FrameDelay = new uint[2];

        private SDL_EventFilter _filter;
        public float scale = 1;
        public static UnityEngine.TouchScreenKeyboard TouchScreenKeyboard; 

        protected override void Initialize()
        {
            Log.Trace("Setup GraphicDeviceManager");

            // _graphicDeviceManager.PreparingDeviceSettings += (sender, e) => e.GraphicsDeviceInformation.PresentationParameters.RenderTargetUsage = RenderTargetUsage.DiscardContents;
            // if (_graphicDeviceManager.GraphicsDevice.Adapter.IsProfileSupported(GraphicsProfile.HiDef))
            //     _graphicDeviceManager.GraphicsProfile = GraphicsProfile.HiDef;

            _graphicDeviceManager.PreferredDepthStencilFormat = DepthFormat.Depth24Stencil8;
            _graphicDeviceManager.SynchronizeWithVerticalRetrace = false; // TODO: V-Sync option
            _graphicDeviceManager.ApplyChanges();


            Window.ClientSizeChanged += WindowOnClientSizeChanged;
            Window.AllowUserResizing = true;
            Window.Title = $"ClassicUO - {CUOEnviroment.Version}";
            IsMouseVisible = Settings.GlobalSettings.RunMouseInASeparateThread;

            IsFixedTimeStep = false; // Settings.GlobalSettings.FixedTimeStep;
            TargetElapsedTime = TimeSpan.FromMilliseconds(1000.0f / 250);

            SetRefreshRate(Settings.GlobalSettings.FPS);
            _uoSpriteBatch = new UltimaBatcher2D(GraphicsDevice);

            _filter = new SDL_EventFilter(HandleSDLEvent);
            SDL.SDL_AddEventWatch(_filter, IntPtr.Zero);

            base.Initialize();
        }

        private readonly Texture2D[] _hues_sampler = new Texture2D[2];

        protected override void LoadContent()
        {
            base.LoadContent();

            Client.Load();

            uint[] buffer = new uint[32 * 3000 * 2];
            HuesLoader.Instance.CreateShaderColors(buffer);


            _hues_sampler[0] = new Texture2D(
                                          GraphicsDevice,
                                          32,
                                          3000);
            _hues_sampler[0].SetData(buffer, 0, buffer.Length / 2);
           
           
            _hues_sampler[1] = new Texture2D(
                                          GraphicsDevice,
                                          32,
                                          3000);
            _hues_sampler[1].SetData(buffer, (buffer.Length / 2) - 1, buffer.Length / 2);


            GraphicsDevice.Textures[1] = _hues_sampler[0];
            GraphicsDevice.Textures[2] = _hues_sampler[1];
            
            // File.WriteAllBytes(Path.Combine(UnityEngine.Application.persistentDataPath, "hue1.png"), UnityEngine.ImageConversion.EncodeToPNG(_hues_sampler[0].UnityTexture as UnityEngine.Texture2D));
            // File.WriteAllBytes(Path.Combine(UnityEngine.Application.persistentDataPath, "hue2.png"), UnityEngine.ImageConversion.EncodeToPNG(_hues_sampler[1].UnityTexture as UnityEngine.Texture2D));

            AuraManager.CreateAuraTexture();
            UIManager.InitializeGameCursor();



            SetScene(new LoginScene());
            SetWindowPositionBySettings();
        }

        public override void UnloadContent()
        {
            SDL.SDL_GetWindowBordersSize(Window.Handle, out int top, out int left, out int bottom, out int right);
            Settings.GlobalSettings.WindowPosition = new Point(Math.Max(0, Window.ClientBounds.X - left), Math.Max(0, Window.ClientBounds.Y - top));
            
            Settings.GlobalSettings.Save();
            Plugin.OnClosing();
            
            _hues_sampler[0]?.Dispose();
            _hues_sampler[0] = null;
            _hues_sampler[1]?.Dispose();
            _hues_sampler[1] = null;
            _scene?.Dispose();
            AuraManager.Dispose();
            UIManager.Dispose();
            Texture2DCache.Dispose();

            base.UnloadContent();
        }

        [MethodImpl(256)]
        public T GetScene<T>() where T : Scene
        {
            return _scene as T;
        }

        public void SetScene(Scene scene)
        {
            _scene?.Dispose();
            _scene = scene;

            //NOTE: Added this to be able to react to scene changes, mainly for calculating render scale factor
            sceneChanged?.Invoke();

            if (scene != null)
            {
                Window.AllowUserResizing = scene.CanResize;
                scene.Load();
            }
        }

        public void SetRefreshRate(int rate)
        {
            if (rate < Constants.MIN_FPS)
                rate = Constants.MIN_FPS;
            else if (rate > Constants.MAX_FPS)
                rate = Constants.MAX_FPS;

            FrameDelay[0] = FrameDelay[1] = (uint) (1000 / rate);
            FrameDelay[1] = FrameDelay[1] >> 1;

            Settings.GlobalSettings.FPS = rate;
            //TargetElapsedTime = TimeSpan.FromMilliseconds(1000.0f / 250);

            _intervalFixedUpdate[0] = 1000.0f / rate;
            _intervalFixedUpdate[1] = 217;  // 5 FPS
        }

        public void SetWindowPosition(int x, int y)
        {
            SDL.SDL_SetWindowPosition(Window.Handle, x, y);
        }

        public GraphicsDeviceManager GraphicManager => _graphicDeviceManager;

        public void SetWindowSize(int width, int height)
        {
            //width = (int) ((double) width * Client.Game.GraphicManager.PreferredBackBufferWidth / Client.Game.Window.ClientBounds.Width);
            //height = (int) ((double) height * Client.Game.GraphicManager.PreferredBackBufferHeight / Client.Game.Window.ClientBounds.Height);

            _graphicDeviceManager.PreferredBackBufferWidth = width;
            _graphicDeviceManager.PreferredBackBufferHeight = height;
            _graphicDeviceManager.ApplyChanges();


            if (CUOEnviroment.IsHighDPI)
            {
                width *= 2;
                height *= 2;
            }

            // _buffer?.Dispose();
            // _buffer = new RenderTarget2D(GraphicsDevice,
            //                              width,
            //                              height,
            //                              false, 
            //                              SurfaceFormat.Color,
            //                              DepthFormat.Depth24Stencil8);
        }

        public void SetWindowBorderless(bool borderless)
        {
            SDL_WindowFlags flags = (SDL_WindowFlags) SDL.SDL_GetWindowFlags(Window.Handle);

            if ((flags & SDL_WindowFlags.SDL_WINDOW_BORDERLESS) != 0 && borderless)
            {
                return;
            }

            if ((flags & SDL_WindowFlags.SDL_WINDOW_BORDERLESS) == 0 && !borderless)
            {
                return;
            }
            
            SDL.SDL_SetWindowBordered(Window.Handle, borderless ? SDL_bool.SDL_FALSE : SDL_bool.SDL_TRUE);

            SDL.SDL_GetCurrentDisplayMode(0, out SDL_DisplayMode displayMode);

            int width = displayMode.w;
            int height = displayMode.h;

            if (borderless)
            {
                SetWindowSize(width, height);
                SDL_SetWindowPosition(Window.Handle, 0, 0);
            }
            else
            {
                int top, left, bottom, right;
                SDL_GetWindowBordersSize(Window.Handle, out top, out left, out bottom, out right);
                SetWindowSize(width, height - (top - bottom));
                SetWindowPositionBySettings();
            }

            var viewport = UIManager.GetGump<WorldViewportGump>();

            if (viewport != null && ProfileManager.Current.GameWindowFullSize)
            {
                viewport.ResizeGameWindow(new Point(width, height));
                viewport.X = -5;
                viewport.Y = -5;
            }
        }

        public void MaximizeWindow()
        {
            SDL.SDL_MaximizeWindow(Window.Handle);
        }

        public bool IsWindowMaximized()
        {
            SDL.SDL_WindowFlags flags = (SDL.SDL_WindowFlags) SDL.SDL_GetWindowFlags(Window.Handle);

            return (flags & SDL_WindowFlags.SDL_WINDOW_MAXIMIZED) != 0;
        }

        public void RestoreWindow()
        {
            SDL.SDL_RestoreWindow(Window.Handle);
        }

        public void SetWindowPositionBySettings()
        {
            SDL_GetWindowBordersSize(Window.Handle, out int top, out int left, out int bottom, out int right);
            if (Settings.GlobalSettings.WindowPosition.HasValue)
            {
                int x = left + Settings.GlobalSettings.WindowPosition.Value.X;
                int y = top + Settings.GlobalSettings.WindowPosition.Value.Y;
                x = Math.Max(0, x);
                y = Math.Max(0, y);

                SetWindowPosition(x, y);
            }
        }

        protected override void Update(GameTime gameTime)
        {
            if (Profiler.InContext("OutOfContext"))
                Profiler.ExitContext("OutOfContext");

            Time.Ticks = (uint) gameTime.TotalGameTime.TotalMilliseconds;

            // Mouse.Update();
            UnityInputUpdate();
            OnNetworkUpdate(gameTime.TotalGameTime.TotalMilliseconds, gameTime.ElapsedGameTime.TotalMilliseconds);
            UIManager.Update(gameTime.TotalGameTime.TotalMilliseconds, gameTime.ElapsedGameTime.TotalMilliseconds);
            Plugin.Tick();

            //var p = Microsoft.Xna.Framework.Input.Mouse.GetState();
            //Console.WriteLine("FNA MOUSE: {0},{1}", p.X, p.Y);
            //Console.WriteLine("SDL MOUSE: {0},{1}", Mouse.Position.X, Mouse.Position.Y);


            if (_scene != null && _scene.IsLoaded && !_scene.IsDestroyed)
            {
                Profiler.EnterContext("Update");
                _scene.Update(gameTime.TotalGameTime.TotalMilliseconds, gameTime.ElapsedGameTime.TotalMilliseconds);
                Profiler.ExitContext("Update");
            }

            _totalElapsed += gameTime.ElapsedGameTime.TotalMilliseconds;
            _currentFpsTime += gameTime.ElapsedGameTime.TotalMilliseconds;

            if (_currentFpsTime >= 1000)
            {
                CUOEnviroment.CurrentRefreshRate = _totalFrames;

                _totalFrames = 0;
                _currentFpsTime = 0;
            }

            double x = _intervalFixedUpdate[!IsActive && ProfileManager.Current != null && ProfileManager.Current.ReduceFPSWhenInactive ? 1 : 0];

            if (_totalElapsed > x)
            {
                if (_scene != null && _scene.IsLoaded && !_scene.IsDestroyed)
                {
                    Profiler.EnterContext("FixedUpdate");
                    _scene.FixedUpdate(gameTime.TotalGameTime.TotalMilliseconds, gameTime.ElapsedGameTime.TotalMilliseconds);
                    Profiler.ExitContext("FixedUpdate");
                }

                _totalElapsed %= x;
            }
            else
            {
                SuppressDraw();

                if (!gameTime.IsRunningSlowly)
                {
                    Thread.Sleep(1);
                }
            }

            base.Update(gameTime);
        }


        protected override void Draw(GameTime gameTime)
        {
            Profiler.EndFrame();
            Profiler.BeginFrame();

            if (Profiler.InContext("OutOfContext"))
                Profiler.ExitContext("OutOfContext");
            Profiler.EnterContext("RenderFrame");

            _totalFrames++;

            if (_scene != null && _scene.IsLoaded && !_scene.IsDestroyed)
                _scene.Draw(_uoSpriteBatch);

            //GraphicsDevice.SetRenderTarget(_buffer);
            UIManager.Draw(_uoSpriteBatch);

            base.Draw(gameTime);

            Profiler.ExitContext("RenderFrame");
            Profiler.EnterContext("OutOfContext");

            //GraphicsDevice.SetRenderTarget(null);
            // _uoSpriteBatch.Begin();
            // _uoSpriteBatch.Draw2D(_buffer, 0, 0, ref _hueVector);
            // _uoSpriteBatch.End();

            UpdateWindowCaption(gameTime);
        }


        private void UpdateWindowCaption(GameTime gameTime)
        {
            if (!Settings.GlobalSettings.Profiler || CUOEnviroment.DisableUpdateWindowCaption)
                return;

            double timeDraw = Profiler.GetContext("RenderFrame").TimeInContext;
            double timeUpdate = Profiler.GetContext("Update").TimeInContext;
            double timeFixedUpdate = Profiler.GetContext("FixedUpdate").TimeInContext;
            double timeOutOfContext = Profiler.GetContext("OutOfContext").TimeInContext;
            //double timeTotalCheck = timeOutOfContext + timeDraw + timeUpdate;
            double timeTotal = Profiler.TrackedTime;
            double avgDrawMs = Profiler.GetContext("RenderFrame").AverageTime;

#if DEV_BUILD
            Window.Title = string.Format("ClassicUO [dev] {5} - Draw:{0:0.0}% Update:{1:0.0}% FixedUpd:{6:0.0} AvgDraw:{2:0.0}ms {3} - FPS: {4}", 100d * (timeDraw / timeTotal), 100d * (timeUpdate / timeTotal), avgDrawMs, gameTime.IsRunningSlowly ? "*" : string.Empty, CUOEnviroment.CurrentRefreshRate, CUOEnviroment.Version, 100d * (timeFixedUpdate / timeTotal));
#else
            Window.Title = string.Format("ClassicUO {5} - Draw:{0:0.0}% Update:{1:0.0}% FixedUpd:{6:0.0} AvgDraw:{2:0.0}ms {3} - FPS: {4}", 100d * (timeDraw / timeTotal), 100d * (timeUpdate / timeTotal), avgDrawMs, gameTime.IsRunningSlowly ? "*" : string.Empty, CUOEnviroment.CurrentRefreshRate, CUOEnviroment.Version, 100d * (timeFixedUpdate / timeTotal));
#endif
        }

        private void OnNetworkUpdate(double totalMS, double frameMS)
        {
            if (NetClient.LoginSocket.IsDisposed && NetClient.LoginSocket.IsConnected)
                NetClient.LoginSocket.Disconnect();
            else if (!NetClient.Socket.IsConnected)
            {
                NetClient.LoginSocket.Update();
                UpdateSockeStats(NetClient.LoginSocket, totalMS);
            }
            else if (!NetClient.Socket.IsDisposed)
            {
                NetClient.Socket.Update();
                UpdateSockeStats(NetClient.Socket, totalMS);
            }
        }

        private void UpdateSockeStats(NetClient socket, double totalMS)
        {
            if (_statisticsTimer < totalMS)
            {
                socket.Statistics.Update();
                _statisticsTimer = totalMS + 500;
            }
        }

        //public override void OnSDLEvent(ref SDL_Event ev)
        //{
        //    HandleSDLEvent(ref ev);
        //    base.OnSDLEvent(ref ev);
        //}

        private void WindowOnClientSizeChanged(object sender, EventArgs e)
        {
            int width = Window.ClientBounds.Width;
            int height = Window.ClientBounds.Height;

            if (!IsWindowMaximized())
            {
                ProfileManager.Current.WindowClientBounds = new Point(width, height);
            }

            SetWindowSize(width, height);

            var viewport = UIManager.GetGump<WorldViewportGump>();

            if (viewport != null && ProfileManager.Current.GameWindowFullSize)
            {
                viewport.ResizeGameWindow(new Point(width, height));
                viewport.X = -5;
                viewport.Y = -5;
            }
        }

        private unsafe int HandleSDLEvent(IntPtr userdata, IntPtr ptr)
        {
            SDL_Event* e = (SDL_Event*) ptr;

            switch (e->type)
            {
                case SDL.SDL_EventType.SDL_AUDIODEVICEADDED:
                    Console.WriteLine("AUDIO ADDED: {0}", e->adevice.which);

                    break;

                case SDL.SDL_EventType.SDL_AUDIODEVICEREMOVED:
                    Console.WriteLine("AUDIO REMOVED: {0}", e->adevice.which);

                    break;


                case SDL.SDL_EventType.SDL_WINDOWEVENT:

                    switch (e->window.windowEvent)
                    {
                        case SDL.SDL_WindowEventID.SDL_WINDOWEVENT_ENTER:
                            Mouse.MouseInWindow = true;

                            break;

                        case SDL.SDL_WindowEventID.SDL_WINDOWEVENT_LEAVE:
                            Mouse.MouseInWindow = false;

                            break;

                        case SDL.SDL_WindowEventID.SDL_WINDOWEVENT_FOCUS_GAINED:
                            Plugin.OnFocusGained();

                            break;

                        case SDL.SDL_WindowEventID.SDL_WINDOWEVENT_FOCUS_LOST:
                            Plugin.OnFocusLost();

                            break;
                    }

                    break;
                
                case SDL.SDL_EventType.SDL_KEYDOWN:
                    
                    Keyboard.OnKeyDown(e->key);

                    if (Plugin.ProcessHotkeys((int) e->key.keysym.sym, (int) e->key.keysym.mod, true))
                    {
                        _ignoreNextTextInput = false;
                        UIManager.KeyboardFocusControl?.InvokeKeyDown(e->key.keysym.sym, e->key.keysym.mod);

                        _scene.OnKeyDown(e->key);
                    }
                    else
                        _ignoreNextTextInput = true;

                    break;

                case SDL.SDL_EventType.SDL_KEYUP:
                    
                    Keyboard.OnKeyUp(e->key);
                    UIManager.KeyboardFocusControl?.InvokeKeyUp(e->key.keysym.sym, e->key.keysym.mod);
                    _scene.OnKeyUp(e->key);
                    Plugin.ProcessHotkeys(0, 0, false);

                    if (e->key.keysym.sym == SDL_Keycode.SDLK_PRINTSCREEN)
                    {
                        string path = Path.Combine(FileSystemHelper.CreateFolderIfNotExists(CUOEnviroment.ExecutablePath, "Data", "Client", "Screenshots"), $"screenshot_{DateTime.Now:yyyy-MM-dd_hh-mm-ss}.png");

                        using (Stream stream = File.Create(path))
                        {
                            //_buffer.SaveAsPng(stream, _buffer.Width, _buffer.Height);

                            GameActions.Print($"Screenshot stored in: {path}", 0x44, MessageType.System);
                        }
                    }

                    break;

                case SDL.SDL_EventType.SDL_TEXTINPUT:

                    if (_ignoreNextTextInput)
                        break;

                    string s = StringHelper.ReadUTF8(e->text.text);

                    if (!string.IsNullOrEmpty(s))
                    {
                        UIManager.KeyboardFocusControl?.InvokeTextInput(s);
                        _scene.OnTextInput(s);
                    }
                    break;

                case SDL.SDL_EventType.SDL_MOUSEMOTION:
                    Mouse.Update();

                    if (Mouse.IsDragging)
                    {
                        UIManager.OnMouseDragging();
                        _scene.OnMouseDragging();
                    }

                    if (Mouse.IsDragging && !_dragStarted)
                    {
                        _dragStarted = true;
                    }

                    break;

                case SDL.SDL_EventType.SDL_MOUSEWHEEL:
                    Mouse.Update();
                    bool isup = e->wheel.y > 0;

                    Plugin.ProcessMouse(0, e->wheel.y);

                    UIManager.OnMouseWheel(isup);
                    _scene.OnMouseWheel(isup);

                    break;

                case SDL.SDL_EventType.SDL_MOUSEBUTTONUP:
                case SDL.SDL_EventType.SDL_MOUSEBUTTONDOWN:
                    Mouse.Update();
                    bool isDown = e->type == SDL.SDL_EventType.SDL_MOUSEBUTTONDOWN;

                    if (_dragStarted && !isDown)
                    {
                        _dragStarted = false;
                    }

                    SDL.SDL_MouseButtonEvent mouse = e->button;

                    switch ((uint) mouse.button)
                    {
                        case SDL_BUTTON_LEFT:

                            if (isDown)
                            {
                                Mouse.Begin();
                                Mouse.LButtonPressed = true;
                                Mouse.LDropPosition = Mouse.Position;
                                Mouse.CancelDoubleClick = false;
                                uint ticks = Time.Ticks;

                                if (Mouse.LastLeftButtonClickTime + Mouse.MOUSE_DELAY_DOUBLE_CLICK >= ticks)
                                {
                                    Mouse.LastLeftButtonClickTime = 0;
                                 
                                    bool res = UIManager.ValidForDClick() ? UIManager.OnLeftMouseDoubleClick() : _scene.OnLeftMouseDoubleClick();

                                    if (!res)
                                    {
                                        _scene.OnLeftMouseDown();
                                        UIManager.OnLeftMouseButtonDown();
                                    }
                                    else
                                    {
                                        Mouse.LastLeftButtonClickTime = 0xFFFF_FFFF;
                                    }

                                    break;
                                }

                                _scene.OnLeftMouseDown();
                                UIManager.OnLeftMouseButtonDown();

                                Mouse.LastLeftButtonClickTime = Mouse.CancelDoubleClick ? 0 : ticks;
                            }
                            else
                            {
                                if (Mouse.LastLeftButtonClickTime != 0xFFFF_FFFF)
                                {
                                    if (!UIManager.HadMouseDownOnGump(MouseButtonType.Left))
                                        _scene.OnLeftMouseUp();
                                    UIManager.OnLeftMouseButtonUp();
                                }
                                Mouse.LButtonPressed = false;
                                Mouse.End();
                            }

                            break;

                        case SDL_BUTTON_MIDDLE:

                            if (isDown)
                            {
                                Mouse.Begin();
                                Mouse.MButtonPressed = true;
                                Mouse.MDropPosition = Mouse.Position;
                                Mouse.CancelDoubleClick = false;
                                uint ticks = Time.Ticks;

                                if (Mouse.LastMidButtonClickTime + Mouse.MOUSE_DELAY_DOUBLE_CLICK >= ticks)
                                {
                                    Mouse.LastMidButtonClickTime = 0;

                                    if (!_scene.OnMiddleMouseDoubleClick())
                                    {
                                        _scene.OnMiddleMouseDown();
                                        UIManager.OnMiddleMouseButtonDown();
                                    }

                                    break;
                                }

                                Plugin.ProcessMouse(e->button.button, 0);

                                _scene.OnMiddleMouseDown();
                                UIManager.OnMiddleMouseButtonDown();

                                Mouse.LastMidButtonClickTime = Mouse.CancelDoubleClick ? 0 : ticks;
                            }
                            else
                            {
                                if (Mouse.LastMidButtonClickTime != 0xFFFF_FFFF)
                                {
                                    if (!UIManager.HadMouseDownOnGump(MouseButtonType.Middle))
                                        _scene.OnMiddleMouseUp();
                                    UIManager.OnMiddleMouseButtonUp();
                                }

                                Mouse.MButtonPressed = false;
                                Mouse.End();
                            }

                            break;

                        case SDL_BUTTON_RIGHT:

                            if (isDown)
                            {
                                Mouse.Begin();
                                Mouse.RButtonPressed = true;
                                Mouse.RDropPosition = Mouse.Position;
                                Mouse.CancelDoubleClick = false;
                                uint ticks = Time.Ticks;

                                if (Mouse.LastRightButtonClickTime + Mouse.MOUSE_DELAY_DOUBLE_CLICK >= ticks)
                                {
                                    Mouse.LastRightButtonClickTime = 0;

                                    bool res = _scene.OnRightMouseDoubleClick() || UIManager.OnRightMouseDoubleClick();

                                    if (!res)
                                    {
                                        _scene.OnRightMouseDown();
                                        UIManager.OnRightMouseButtonDown();
                                    }
                                    else
                                    {
                                        Mouse.LastRightButtonClickTime = 0xFFFF_FFFF;
                                    }

                                    break;
                                }

                                _scene.OnRightMouseDown();
                                UIManager.OnRightMouseButtonDown();

                                Mouse.LastRightButtonClickTime = Mouse.CancelDoubleClick ? 0 : ticks;
                            }
                            else
                            {
                                if (Mouse.LastRightButtonClickTime != 0xFFFF_FFFF)
                                {
                                    if (!UIManager.HadMouseDownOnGump(MouseButtonType.Right))
                                        _scene.OnRightMouseUp();
                                    UIManager.OnRightMouseButtonUp();
                                }
                                Mouse.RButtonPressed = false;
                                Mouse.End();
                            }

                            break;

                        case SDL_BUTTON_X1:
                        case SDL_BUTTON_X2:
                            if (isDown)
                            {
                                Mouse.Begin();
                                Mouse.XButtonPressed = true;
                                Mouse.CancelDoubleClick = false;
                                Plugin.ProcessMouse(e->button.button, 0);
                                _scene.OnExtraMouseDown(mouse.button - 1);
                                UIManager.OnExtraMouseButtonDown(mouse.button - 1);
                            }
                            else
                            {
                                if (!UIManager.HadMouseDownOnGump(MouseButtonType.XButton1) && !UIManager.HadMouseDownOnGump(MouseButtonType.XButton2))
                                    _scene.OnExtraMouseUp(mouse.button - 1);
                                UIManager.OnExtraMouseButtonUp(mouse.button - 1);

                                Mouse.XButtonPressed = false;
                                Mouse.End();
                            }

                            break;
                    }

                    break;
            }

            return 0;
        }

        private readonly UnityEngine.KeyCode[] _keyCodeEnumValues = (UnityEngine.KeyCode[]) Enum.GetValues(typeof(UnityEngine.KeyCode));
        private UnityEngine.Vector3 lastMousePosition;
        private bool mouseWasDown = false;

        private void UnityInputUpdate()
        {
            //Mouse handling
            var oneOverScale = 1f / scale;

            Mouse.Position.X = UnityEngine.Mathf.RoundToInt(UnityEngine.Input.mousePosition.x * oneOverScale);
            Mouse.Position.Y = UnityEngine.Mathf.RoundToInt((UnityEngine.Screen.height - UnityEngine.Input.mousePosition.y) * oneOverScale);
            Mouse.RealPosition = Mouse.Position;

            var leftMouseDown = UnityEngine.Input.GetMouseButtonDown(0);
            var leftMouseHeld = UnityEngine.Input.GetMouseButton(0);
            var leftMouseUp = UnityEngine.Input.GetMouseButtonUp(0);

            var rightMouseDown = UnityEngine.Input.GetMouseButtonDown(1);
            var rightMouseHeld = UnityEngine.Input.GetMouseButton(1);
            var rightMouseUp = UnityEngine.Input.GetMouseButtonUp(1);

            if (MouseOverGui)
            {
                Mouse.Position.X = 0;
                Mouse.Position.Y = 0;
                leftMouseDown = false;
                leftMouseHeld = false;
                leftMouseUp = false;
                rightMouseDown = false;
                rightMouseHeld = false;
                rightMouseUp = false;
            }

            Mouse.LButtonPressed = leftMouseDown || leftMouseHeld;
            Mouse.RButtonPressed = rightMouseDown || rightMouseHeld;
            Mouse.MButtonPressed = UnityEngine.Input.GetMouseButtonDown(2) || UnityEngine.Input.GetMouseButton(2);

            Mouse.IsDragging = Mouse.LButtonPressed || Mouse.RButtonPressed || Mouse.MButtonPressed;
            Mouse.RealPosition = Mouse.Position;

            //Added additional check for mouseWasDown because otherwise, on mobile, tapping somewhere would immediately
            //set mouseMotion to true since the previous tap position was almost guaranteed to be at another position
            //due to input inaccuracy. We do not want a new tap to be recognized as a mouseMotion.
            var mouseMotion = UnityEngine.Input.mousePosition != lastMousePosition && mouseWasDown;
            
            lastMousePosition = UnityEngine.Input.mousePosition;

            if (_dragStarted && !Mouse.LButtonPressed)
            {
                _dragStarted = false;
            }

            if (leftMouseDown)
            {
                mouseWasDown = true;
                Mouse.Begin();
                Mouse.LDropPosition = Mouse.Position;
                Mouse.CancelDoubleClick = false;
                uint ticks = Time.Ticks;
                if (Mouse.LastLeftButtonClickTime + Mouse.MOUSE_DELAY_DOUBLE_CLICK >= ticks)
                {
                    Mouse.LastLeftButtonClickTime = 0;

                    bool res = UIManager.ValidForDClick() ? UIManager.OnLeftMouseDoubleClick() : _scene.OnLeftMouseDoubleClick();

                    if (!res)
                    {
                        _scene.OnLeftMouseDown();
                        UIManager.OnLeftMouseButtonDown();
                    }
                    else
                    {
                        Mouse.LastLeftButtonClickTime = 0xFFFF_FFFF;
                    }
                }
                else
                {
                    _scene.OnLeftMouseDown();
                    UIManager.OnLeftMouseButtonDown();
                    Mouse.LastLeftButtonClickTime = Mouse.CancelDoubleClick ? 0 : ticks;
                }
            }
            else if(leftMouseUp)
            {
                mouseWasDown = false;
                if (Mouse.LastLeftButtonClickTime != 0xFFFF_FFFF)
                {
                    if (!UIManager.HadMouseDownOnGump(MouseButtonType.Left))
                        _scene.OnLeftMouseUp();
                    UIManager.OnLeftMouseButtonUp();
                }
                Mouse.End();
            }

            if (rightMouseDown)
            {
                Mouse.Begin();
                Mouse.RDropPosition = Mouse.Position;
                Mouse.CancelDoubleClick = false;
                uint ticks = Time.Ticks;

                if (Mouse.LastRightButtonClickTime + Mouse.MOUSE_DELAY_DOUBLE_CLICK >= ticks)
                {
                    Mouse.LastRightButtonClickTime = 0;

                    bool res = _scene.OnRightMouseDoubleClick() || UIManager.OnRightMouseDoubleClick();

                    if (!res)
                    {
                        _scene.OnRightMouseDown();
                        UIManager.OnRightMouseButtonDown();
                    }
                    else
                    {
                        Mouse.LastRightButtonClickTime = 0xFFFF_FFFF;
                    }
                }
                else
                {
                    _scene.OnRightMouseDown();
                    UIManager.OnRightMouseButtonDown();
                    Mouse.LastRightButtonClickTime = Mouse.CancelDoubleClick ? 0 : ticks;
                }
            }
            else if(rightMouseUp)
            {
                if (Mouse.LastRightButtonClickTime != 0xFFFF_FFFF)
                {
                    if (!UIManager.HadMouseDownOnGump(MouseButtonType.Right))
                        _scene.OnRightMouseUp();
                    UIManager.OnRightMouseButtonUp();
                }
                Mouse.End();
            }

            if (mouseMotion)
            {
                if (Mouse.IsDragging)
                {
                    UIManager.OnMouseDragging();
                    _scene.OnMouseDragging();
                }

                if (Mouse.IsDragging && !_dragStarted)
                {
                    _dragStarted = true;
                }
            }

            //Keyboard handling

            var keymod = SDL.SDL_Keymod.KMOD_NONE;
            if (UnityEngine.Input.GetKey(UnityEngine.KeyCode.LeftAlt))
            {
                keymod |= SDL_Keymod.KMOD_LALT;
            }
            if (UnityEngine.Input.GetKey(UnityEngine.KeyCode.RightAlt))
            {
                keymod |= SDL_Keymod.KMOD_RALT;
            }
            if (UnityEngine.Input.GetKey(UnityEngine.KeyCode.LeftShift))
            {
                keymod |= SDL_Keymod.KMOD_LSHIFT;
            }
            if (UnityEngine.Input.GetKey(UnityEngine.KeyCode.RightShift))
            {
                keymod |= SDL_Keymod.KMOD_RSHIFT;
            }
            if (UnityEngine.Input.GetKey(UnityEngine.KeyCode.LeftControl))
            {
                keymod |= SDL_Keymod.KMOD_LCTRL;
            }
            if (UnityEngine.Input.GetKey(UnityEngine.KeyCode.RightControl))
            {
                keymod |= SDL_Keymod.KMOD_RCTRL;
            }
            foreach (var keyCode in _keyCodeEnumValues)
            {
                if (UnityEngine.Input.GetKeyDown(keyCode))
                {
                    var key = new SDL_KeyboardEvent() {keysym = new SDL_Keysym() {sym = (SDL_Keycode) keyCode, mod = keymod}};
                    Keyboard.OnKeyDown(key);

                    if (Plugin.ProcessHotkeys((int) key.keysym.sym, (int) key.keysym.mod, true))
                    {
                        _ignoreNextTextInput = false;

                        //UIManager.MouseOverControl?.InvokeKeyDown(e.key.keysym.sym, e.key.keysym.mod);
                        //if (UIManager.MouseOverControl != UIManager.KeyboardFocusControl)
                        UIManager.KeyboardFocusControl?.InvokeKeyDown(key.keysym.sym, key.keysym.mod);

                        _scene.OnKeyDown(key);
                    }
                    else
                        _ignoreNextTextInput = true;
                }
                if (UnityEngine.Input.GetKeyUp(keyCode))
                {
                    var key = new SDL_KeyboardEvent() {keysym = new SDL_Keysym() {sym = (SDL_Keycode) keyCode}};
                    Keyboard.OnKeyUp(key);
                    UIManager.KeyboardFocusControl?.InvokeKeyUp(key.keysym.sym, key.keysym.mod);
                    _scene.OnKeyUp(key);
                }
            }

            //Input text handling
            if (UnityEngine.Application.isMobilePlatform)
            {
                var text = TouchScreenKeyboard?.text;
                if (_ignoreNextTextInput == false && string.IsNullOrEmpty(text) == false && TouchScreenKeyboard?.status == UnityEngine.TouchScreenKeyboard.Status.Done)
                {
                    //Need to clear the existing text in textbox before "pasting" new text from TouchScreenKeyboard
                    if (UIManager.KeyboardFocusControl is AbstractTextBox abstractTextBox)
                    {
                        abstractTextBox.EntryValue.Clear();
                    }
                    
                    UIManager.KeyboardFocusControl?.InvokeTextInput(text);
                    _scene.OnTextInput(text);
                    
                    //When targeting SystemChat textbox, "auto-press" return key so that the text entered on the TouchScreenKeyboard is submitted right away
                    if (UIManager.KeyboardFocusControl != null && UIManager.KeyboardFocusControl == UIManager.SystemChat?.TextBoxControl)
                    {
                        UIManager.KeyboardFocusControl.InvokeKeyDown(SDL_Keycode.SDLK_RETURN, SDL_Keymod.KMOD_NONE);
                    }
                    
                    //Clear the text of TouchScreenKeyboard, otherwise it stays there and is re-evaluated every frame
                    TouchScreenKeyboard.text = string.Empty;
                }
            }
            else
            {
                var text = UnityEngine.Input.inputString;
                if (_ignoreNextTextInput == false && string.IsNullOrEmpty(text) == false)
                {
                    UIManager.KeyboardFocusControl?.InvokeTextInput(text);
                    _scene.OnTextInput(text);
                }
            }
        }
    }
}
