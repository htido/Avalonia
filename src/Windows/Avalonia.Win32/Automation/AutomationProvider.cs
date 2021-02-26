﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Automation;
using Avalonia.Controls.Automation.Peers;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Win32.Interop.Automation;

#nullable enable

namespace Avalonia.Win32.Automation
{
    [ComVisible(true)]
    internal partial class AutomationProvider : MarshalByRefObject,
        IAutomationPeerImpl,
        IRawElementProviderSimple,
        IRawElementProviderSimple2,
        IRawElementProviderFragment,
        IInvokeProvider
    {
        private readonly UiaControlTypeId _controlType;
        private readonly string _localizedControlType;
        private readonly bool _isControlElement;
        private readonly WindowImpl _visualRoot;
        private readonly int[] _runtimeId;
        private AutomationProvider? _parent;
        private bool _isParentInitialized;
        private Rect _boundingRect;
        private List<AutomationProvider>? _children;
        private bool _childrenValid;
        private string? _className;
        private IRawElementProviderFragmentRoot? _fragmentRoot;
        private bool _hasKeyboardFocus;
        private bool _isKeyboardFocusable;
        private bool _isEnabled;
        private string? _name;
        private bool _isDisposed;

        public AutomationProvider(ControlAutomationPeer peer)
        {
            Dispatcher.UIThread.VerifyAccess();

            Peer = peer;

            _controlType = RoleToControlType(peer.GetRole());
            _localizedControlType = peer.GetLocalizedControlType();
            _isControlElement = peer.IsControlElement();
            _visualRoot = (WindowImpl)GetRoot(peer.Owner).PlatformImpl;
            _runtimeId = new int[] { 3, Peer.GetHashCode() };
        }

        public ControlAutomationPeer Peer { get; }

        public Rect BoundingRectangle 
        { 
            get
            {
                return new PixelRect(
                    _visualRoot.PointToScreen(_boundingRect.TopLeft),
                    _visualRoot.PointToScreen(_boundingRect.BottomRight))
                    .ToRect(1);
            }
        }

        public virtual IRawElementProviderFragmentRoot? FragmentRoot 
        { 
            get
            {
                return _fragmentRoot ??= InvokeSync(() =>
                {
                    var root = GetRoot(Peer.Owner);
                    var rootPeer = ControlAutomationPeer.GetOrCreatePeer(root);
                    return (IRawElementProviderFragmentRoot)rootPeer.PlatformImpl;
                });
            }
        }

        public ProviderOptions ProviderOptions => ProviderOptions.ServerSideProvider;
        public virtual IRawElementProviderSimple? HostRawElementProvider => null;

        public void Dispose()
        {
            // Feels like we should be calling UiaDisconnectProvider here, but that slows things
            // down HORRIBLY and looking through the WPF codebase seems that function is never
            // called there.
            _isDisposed = true;
        }
        
        public void PropertyChanged() 
        {
            if (_isDisposed)
                return;

            Dispatcher.UIThread.VerifyAccess();
            UpdateCore(true);
        }
        
        public void StructureChanged() 
        {
            if (_isDisposed)
                return;

            _childrenValid = false;
            UiaCoreProviderApi.UiaRaiseStructureChangedEvent(
                this,
                StructureChangeType.ChildrenInvalidated,
                _runtimeId,
                _runtimeId.Length);
        }

        [return: MarshalAs(UnmanagedType.IUnknown)]
        public virtual object? GetPatternProvider(int patternId)
        {
            if (_isDisposed)
                return null;

            return (UiaPatternId)patternId switch
            {
                UiaPatternId.ExpandCollapse => Peer is IOpenCloseAutomationPeer ? this : null,
                UiaPatternId.Invoke => Peer is IInvocableAutomationPeer ? this : null,
                UiaPatternId.RangeValue => Peer is IRangeValueAutomationPeer ? this : null,
                UiaPatternId.Scroll => Peer is IScrollableAutomationPeer ? this : null,
                UiaPatternId.ScrollItem => this,
                UiaPatternId.Selection => Peer is ISelectingAutomationPeer ? this : null,
                UiaPatternId.SelectionItem => Peer is ISelectableAutomationPeer ? this : null,
                UiaPatternId.Toggle => Peer is IToggleableAutomationPeer ? this : null,
                UiaPatternId.Value => Peer is IStringValueAutomationPeer ? this : null,
                _ => null,
            };
        }

        public virtual object? GetPropertyValue(int propertyId)
        {
            if (_isDisposed)
                return null;

            return (UiaPropertyId)propertyId switch
            {
                UiaPropertyId.ClassName => _className,
                UiaPropertyId.ClickablePoint => new[] { BoundingRectangle.Center.X, BoundingRectangle.Center.Y },
                UiaPropertyId.ControlType => _controlType,
                UiaPropertyId.Culture => CultureInfo.CurrentCulture.LCID,
                UiaPropertyId.FrameworkId => "Avalonia",
                UiaPropertyId.HasKeyboardFocus => _hasKeyboardFocus,
                UiaPropertyId.IsContentElement => _isControlElement,
                UiaPropertyId.IsControlElement => _isControlElement,
                UiaPropertyId.IsEnabled => _isEnabled,
                UiaPropertyId.IsKeyboardFocusable => _isKeyboardFocusable,
                UiaPropertyId.LocalizedControlType => _localizedControlType,
                UiaPropertyId.Name => _name,
                UiaPropertyId.ProcessId => Process.GetCurrentProcess().Id,
                UiaPropertyId.RuntimeId => _runtimeId,
                _ => null,
            };
        }

        public int[]? GetRuntimeId() => _runtimeId;

        public virtual IRawElementProviderFragment? Navigate(NavigateDirection direction)
        {
            if (_isDisposed)
                return null;

            if (direction == NavigateDirection.Parent)
            {
                return GetParent();
            }

            EnsureChildren();

            return direction switch
            {
                NavigateDirection.NextSibling => GetParent()?.GetSibling(this, 1),
                NavigateDirection.PreviousSibling => GetParent()?.GetSibling(this, -1),
                NavigateDirection.FirstChild => _children?.FirstOrDefault(),
                NavigateDirection.LastChild => _children?.LastOrDefault(),
                _ => null,
            };
        }

        public void SetFocus()
        {
            if (_isDisposed)
                return;

            InvokeSync(() => Peer.SetFocus());
        }

        public async Task Update(bool notify)
        {
            if (Dispatcher.UIThread.CheckAccess())
                UpdateCore(notify);
            else
                await Dispatcher.UIThread.InvokeAsync(() => Update(notify));
        }

        public override string ToString() => _className!;
        IRawElementProviderSimple[]? IRawElementProviderFragment.GetEmbeddedFragmentRoots() => null;
        void IRawElementProviderSimple2.ShowContextMenu() => InvokeSync(() => Peer.ShowContextMenu());

        void IInvokeProvider.Invoke() => InvokeSync<IInvocableAutomationPeer>(x => x.Invoke());

        public void UpdateFocus(bool notify)
        {
            UpdateProperty(UiaPropertyId.HasKeyboardFocus, ref _hasKeyboardFocus, Peer.HasKeyboardFocus(), notify);
        }

        protected void InvokeSync(Action action)
        {
            if (_isDisposed)
                return;

            if (Dispatcher.UIThread.CheckAccess())
            {
                action();
            }
            else
            {
                Dispatcher.UIThread.InvokeAsync(action).Wait();
            }
        }

        [return: MaybeNull]
        protected T InvokeSync<T>(Func<T> func)
        {
            if (_isDisposed)
                return default;

            if (Dispatcher.UIThread.CheckAccess())
            {
                return func();
            }
            else
            {
                return Dispatcher.UIThread.InvokeAsync(func).Result;
            }
        }

        protected void InvokeSync<TInterface>(Action<TInterface> action)
        {
            if (Peer is TInterface i)
            {
                try
                {
                    InvokeSync(() => action(i));
                }
                catch (AggregateException e) when (e.InnerException is ElementNotEnabledException)
                {
                    throw new COMException(e.Message, UiaCoreProviderApi.UIA_E_ELEMENTNOTENABLED);
                }
            }
        }

        [return: MaybeNull]
        protected TResult InvokeSync<TInterface, TResult>(Func<TInterface, TResult> func)
        {
            if (Peer is TInterface i)
            {
                try
                {
                    return InvokeSync(() => func(i));
                }
                catch (AggregateException e) when (e.InnerException is ElementNotEnabledException)
                {
                    throw new COMException(e.Message, UiaCoreProviderApi.UIA_E_ELEMENTNOTENABLED);
                }
            }

            return default;
        }

        protected virtual void UpdateCore(bool notify)
        {
            _className = Peer.GetClassName();

            UpdateProperty(UiaPropertyId.BoundingRectangle, ref _boundingRect, Peer.GetBoundingRectangle(), notify);
            UpdateProperty(UiaPropertyId.IsKeyboardFocusable, ref _isKeyboardFocusable, Peer.IsKeyboardFocusable(), notify);
            UpdateProperty(UiaPropertyId.IsEnabled, ref _isEnabled, Peer.IsEnabled(), notify);
            UpdateProperty(UiaPropertyId.Name, ref _name, Peer.GetName(), notify);
            UpdateFocus(notify);
            UpdateExpandCollapse(notify);
            UpdateRangeValue(notify);
            UpdateScroll(notify);
            UpdateSelection(notify);
            UpdateToggle(notify);
            UpdateValue(notify);
        }

        private void UpdateProperty<T>(UiaPropertyId id, ref T _field, T value, bool notify)
        {
            if (!EqualityComparer<T>.Default.Equals(_field, value))
            {
                _field = value;
                if (notify)
                    UiaCoreProviderApi.UiaRaiseAutomationPropertyChangedEvent(this, (int)id, null, null);
            }
        }

        private void EnsureChildren()
        {
            if (!_childrenValid)
            {
                InvokeSync(() => LoadChildren());
                _childrenValid = true;
            }
        }

        private void LoadChildren()
        {
            var childPeers = InvokeSync(() => Peer.GetChildren());

            _children?.Clear();

            if (childPeers is null)
                return;

            foreach (var childPeer in childPeers)
            {
                _children ??= new List<AutomationProvider>();

                if (childPeer.PlatformImpl is AutomationProvider child)
                {
                    _children.Add(child);
                }
                else
                {
                    throw new AvaloniaInternalException(
                        "AutomationPeer platform implementation not recognised.");
                }
            }
        }

        private AutomationProvider? GetParent()
        {
            if (!_isParentInitialized)
            {
                _parent = InvokeSync(() => Peer.GetParent())?.PlatformImpl as AutomationProvider;
                _isParentInitialized = true;
            }

            return _parent;
        }

        private AutomationProvider? GetSibling(AutomationProvider child, int direction)
        {
            EnsureChildren();

            var index = _children?.IndexOf(child) ?? -1;

            if (index >= 0)
            {
                index += direction;

                if (index >= 0 && index < _children!.Count)
                {
                    return _children[index];
                }
            }

            return null;
        }

        private static WindowBase GetRoot(Control control)
        {
            return control.GetVisualRoot() as WindowBase ??
                throw new AvaloniaInternalException("Cannot create automation provider for non-rooted control.");
        }

        private static UiaControlTypeId RoleToControlType(AutomationRole role)
        {
            return role switch
            {
                AutomationRole.Button => UiaControlTypeId.Button,
                AutomationRole.CheckBox => UiaControlTypeId.CheckBox,
                AutomationRole.ComboBox => UiaControlTypeId.ComboBox,
                AutomationRole.Edit => UiaControlTypeId.Edit,
                AutomationRole.Group => UiaControlTypeId.Group,
                AutomationRole.List => UiaControlTypeId.List,
                AutomationRole.ListItem => UiaControlTypeId.ListItem,
                AutomationRole.Menu => UiaControlTypeId.Menu,
                AutomationRole.MenuItem => UiaControlTypeId.MenuItem,
                AutomationRole.Slider => UiaControlTypeId.Slider,
                AutomationRole.TabControl => UiaControlTypeId.Tab,
                AutomationRole.TabItem => UiaControlTypeId.TabItem,
                AutomationRole.Text => UiaControlTypeId.Text,
                AutomationRole.Toggle => UiaControlTypeId.Button,
                AutomationRole.Window => UiaControlTypeId.Window,
                _ => UiaControlTypeId.Custom,
            };
        }
    }
}
