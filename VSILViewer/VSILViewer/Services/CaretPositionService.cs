using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Task = System.Threading.Tasks.Task;

namespace VSILViewer.Services
{
    public class CaretPositionService : IDisposable
    {
        private IWpfTextView? _currentTextView;
        private CancellationTokenSource? _debounceCts;
        private readonly TimeSpan _debounceDelay = TimeSpan.FromMilliseconds(300);

        public event EventHandler? CaretPositionChanged;

        public bool HasTextView => _currentTextView != null;

        public void AttachToTextView(IWpfTextView textView)
        {
            if (textView == null) return;

            // Don't reattach to the same view
            if (_currentTextView == textView) return;

            DetachFromTextView();

            _currentTextView = textView;
            _currentTextView.Caret.PositionChanged += OnCaretPositionChanged;
            _currentTextView.Closed += OnTextViewClosed;

            // Trigger initial update
            RaiseCaretPositionChanged(textView.Caret.Position.BufferPosition.Position);
        }

        public void DetachFromTextView()
        {
            if (_currentTextView != null)
            {
                _currentTextView.Caret.PositionChanged -= OnCaretPositionChanged;
                _currentTextView.Closed -= OnTextViewClosed;
                _currentTextView = null;
            }

            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = null;
        }

        private void OnTextViewClosed(object? sender, EventArgs e)
        {
            DetachFromTextView();
        }

        private void OnCaretPositionChanged(object? sender, Microsoft.VisualStudio.Text.Editor.CaretPositionChangedEventArgs e)
        {
            var position = e.NewPosition.BufferPosition.Position;
            DebounceCaretChange(position);
        }

        private void DebounceCaretChange(int position)
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = new CancellationTokenSource();

            var token = _debounceCts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(_debounceDelay, token);
                    if (!token.IsCancellationRequested)
                    {
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
                        RaiseCaretPositionChanged(position);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Expected when debouncing
                }
            }, token);
        }

        private void RaiseCaretPositionChanged(int position)
        {
            CaretPositionChanged?.Invoke(this, EventArgs.Empty);
        }

        public IWpfTextView? GetCurrentTextView()
        {
            return _currentTextView;
        }

        public void Dispose()
        {
            DetachFromTextView();
        }
    }
}
