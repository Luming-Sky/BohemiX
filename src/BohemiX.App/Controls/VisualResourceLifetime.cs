using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace BohemiX.App.Controls;

public interface IVisualResourceOwner
{
    void ActivateVisualResources();

    void DeactivateVisualResources();
}

/// <summary>
/// Keeps image-backed view models active only while an item container is visible.
/// </summary>
public sealed class VisualResourceLifetime
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<VisualResourceLifetime, Control, bool>("IsEnabled");

    private static readonly ConditionalWeakTable<Control, SubscriptionState> States = new();
    private static readonly ConditionalWeakTable<IVisualResourceOwner, OwnerReference> OwnerReferences = new();

    static VisualResourceLifetime()
    {
        IsEnabledProperty.Changed.AddClassHandler<Control>(OnIsEnabledChanged);
    }

    public static bool GetIsEnabled(Control control) => control.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(Control control, bool value) => control.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(Control control, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.GetNewValue<bool>())
        {
            var state = States.GetValue(control, static item => new SubscriptionState(item));
            state.Refresh();
            return;
        }

        if (States.TryGetValue(control, out var existing))
        {
            existing.Dispose();
            States.Remove(control);
        }
    }

    private static void AddReference(IVisualResourceOwner owner)
    {
        var reference = OwnerReferences.GetValue(owner, static item => new OwnerReference(item));
        reference.AddReference();
    }

    private static void RemoveReference(IVisualResourceOwner owner)
    {
        if (OwnerReferences.TryGetValue(owner, out var reference))
        {
            reference.RemoveReference();
        }
    }

    private sealed class SubscriptionState : IDisposable
    {
        private readonly Control control;
        private readonly List<Visual> visibilityAncestors = [];
        private IVisualResourceOwner? activeOwner;
        private bool disposed;

        public SubscriptionState(Control control)
        {
            this.control = control;
            control.AttachedToVisualTree += OnVisualTreeChanged;
            control.DetachedFromVisualTree += OnVisualTreeChanged;
            control.DataContextChanged += OnDataContextChanged;
            control.PropertyChanged += OnPropertyChanged;
            UpdateVisibilityAncestorSubscriptions();
        }

        public void Refresh()
        {
            if (disposed)
            {
                return;
            }

            var nextOwner = control.IsAttachedToVisualTree() && IsVisibleInVisualTree()
                ? control.DataContext as IVisualResourceOwner
                : null;
            if (ReferenceEquals(activeOwner, nextOwner))
            {
                return;
            }

            if (activeOwner is not null)
            {
                RemoveReference(activeOwner);
            }

            activeOwner = nextOwner;
            if (activeOwner is not null)
            {
                AddReference(activeOwner);
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            control.AttachedToVisualTree -= OnVisualTreeChanged;
            control.DetachedFromVisualTree -= OnVisualTreeChanged;
            control.DataContextChanged -= OnDataContextChanged;
            control.PropertyChanged -= OnPropertyChanged;
            RemoveVisibilityAncestorSubscriptions();
            if (activeOwner is not null)
            {
                RemoveReference(activeOwner);
                activeOwner = null;
            }
        }

        private void OnVisualTreeChanged(object? sender, VisualTreeAttachmentEventArgs e)
        {
            UpdateVisibilityAncestorSubscriptions();
            Refresh();
        }

        private void OnDataContextChanged(object? sender, EventArgs e) => Refresh();

        private void OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (string.Equals(e.Property.Name, nameof(Visual.IsVisible), StringComparison.Ordinal))
            {
                Refresh();
            }
        }

        private void OnVisibilityAncestorPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (string.Equals(e.Property.Name, nameof(Visual.IsVisible), StringComparison.Ordinal))
            {
                Refresh();
            }
        }

        private bool IsVisibleInVisualTree()
        {
            return control.IsVisible && visibilityAncestors.All(ancestor => ancestor.IsVisible);
        }

        private void UpdateVisibilityAncestorSubscriptions()
        {
            RemoveVisibilityAncestorSubscriptions();
            if (!control.IsAttachedToVisualTree())
            {
                return;
            }

            foreach (var ancestor in control.GetVisualAncestors())
            {
                ancestor.PropertyChanged += OnVisibilityAncestorPropertyChanged;
                visibilityAncestors.Add(ancestor);
            }
        }

        private void RemoveVisibilityAncestorSubscriptions()
        {
            foreach (var ancestor in visibilityAncestors)
            {
                ancestor.PropertyChanged -= OnVisibilityAncestorPropertyChanged;
            }

            visibilityAncestors.Clear();
        }
    }

    private sealed class OwnerReference
    {
        private readonly IVisualResourceOwner owner;
        private int count;

        public OwnerReference(IVisualResourceOwner owner)
        {
            this.owner = owner;
        }

        public void AddReference()
        {
            if (count++ == 0)
            {
                owner.ActivateVisualResources();
            }
        }

        public void RemoveReference()
        {
            if (count == 0 || --count != 0)
            {
                return;
            }

            owner.DeactivateVisualResources();
        }
    }
}
