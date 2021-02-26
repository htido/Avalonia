﻿using System;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;

#nullable enable

namespace Avalonia.Controls.Automation.Peers
{
    public class PopupRootAutomationPeer : WindowBaseAutomationPeer
    {
        public PopupRootAutomationPeer(PopupRoot owner)
            : base(owner)
        {
            if (owner.IsVisible)
                StartTrackingFocus();
            else
                owner.Opened += OnOpened;
            owner.Closed += OnClosed;
        }

        protected override AutomationPeer? GetParentCore()
        {
            if (Owner.GetLogicalParent() is Control parent &&
                ((IVisual)parent).IsAttachedToVisualTree)
            {
                return GetOrCreatePeer(parent);
            }

            return null;
        }

        private void OnOpened(object sender, EventArgs e)
        {
            ((PopupRoot)Owner).Opened -= OnOpened;
            StartTrackingFocus();
        }

        private void OnClosed(object sender, EventArgs e)
        {
            ((PopupRoot)Owner).Closed -= OnClosed;
            StopTrackingFocus();
            InvalidatePlatformImpl();
        }
    }
}
