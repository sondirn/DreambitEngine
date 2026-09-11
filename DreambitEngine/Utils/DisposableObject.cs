using System;

namespace Dreambit;

public abstract class DisposableObject : IDisposable
{
    private bool _isDisposed;

    public void Dispose()
    {
        try
        {
            Dispose(true);
        }
        finally
        {
            GC.SuppressFinalize(this);
        }
    }

    ~DisposableObject()
    {
        Dispose(false);
    }

    protected virtual void CleanUp()
    {
    }

    private void Dispose(bool disposing)
    {
        if (_isDisposed)
            return;

        try
        {
            if (disposing)
                CleanUp();
        }
        finally
        {
            // Disposal is terminal even if CleanUp throws.
            _isDisposed = true;
        }
    }
}