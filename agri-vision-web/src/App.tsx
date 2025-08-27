import { useState, useRef, useCallback } from 'react';
import type { FC, ChangeEvent, DragEvent, FormEvent, MouseEvent } from 'react';
import './App.css'; // Make sure you have the corresponding CSS file

const App: FC = () => {
  const apiBase = import.meta.env?.VITE_API_BASE || '';
  const [imageFile, setImageFile] = useState<File | null>(null);
  const [previewUrl, setPreviewUrl] = useState<string>('');
  const [question, setQuestion] = useState<string>('What crop is this and what are the potential issues?');
  const [answer, setAnswer] = useState<string>('');
  const [loading, setLoading] = useState<boolean>(false);
  const [error, setError] = useState<string | null>(null);

  // useRef to access the hidden file input
  const fileInputRef = useRef<HTMLInputElement>(null);

  const handleFileChange = (e: ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    if (file) {
      setImageFile(file);
      setPreviewUrl(URL.createObjectURL(file));
      setError(null);
    }
  };

  const handleClearImage = (e: MouseEvent<HTMLButtonElement>) => {
    e.stopPropagation(); // Prevent triggering the file input
    setImageFile(null);
    setPreviewUrl('');
    if (fileInputRef.current) {
      fileInputRef.current.value = ''; // Reset the file input
    }
  };

  const handleDragOver = (e: DragEvent<HTMLDivElement>) => {
    e.preventDefault();
  };

  const handleDrop = (e: DragEvent<HTMLDivElement>) => {
    e.preventDefault();
    const file = e.dataTransfer.files?.[0];
    if (file && file.type.startsWith('image/')) {
      setImageFile(file);
      setPreviewUrl(URL.createObjectURL(file));
      setError(null);
    }
  };

  const onSubmit = useCallback(async (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    setError(null);
    setAnswer('');

    if (!imageFile) {
      setError('Please select an image file.');
      return;
    }

    const form = new FormData();
    form.append('image', imageFile);
    form.append('question', question);
    setLoading(true);

    try {
      const res = await fetch(`${apiBase}/api/analyze`, {
        method: 'POST',
        body: form,
      });

      if (!res.ok) {
        let message = `Request failed with status: ${res.status}`;
        try {
          const isJson = (res.headers.get('content-type') || '').includes('application/json');
          const problem = isJson ? await res.json() : await res.text();
          message = problem?.detail || problem?.title || (typeof problem === 'string' ? problem : message);
        } catch {
          // Ignore parsing errors
        }
        if (res.status === 429) {
          message = message || 'Quota exceeded. Please check your plan and billing details.';
        }
        throw new Error(message);
      }
      const data = await res.json();
      setAnswer(data.answer || 'No answer received from the AI.');
    } catch (err: unknown) {
        if (err instanceof Error) {
            setError(err.message);
        } else {
            setError('An unknown error occurred.');
        }
    } finally {
      setLoading(false);
    }
  }, [imageFile, question, apiBase]);

  return (
    <div className="app-container">
      <header>
        <h1>Agricultural AI Analyst</h1>
        <p>Upload a farm-related image to get expert insights from our AI.</p>
      </header>

      <main>
        <form onSubmit={onSubmit} className="analysis-form">
          <div
            className="file-input-area"
            onClick={() => fileInputRef.current?.click()}
            onDragOver={handleDragOver}
            onDrop={handleDrop}
          >
            <input
              type="file"
              accept="image/*"
              onChange={handleFileChange}
              ref={fileInputRef}
              aria-label="File upload"
              style={{ display: 'none' }}
            />
            {previewUrl ? (
              <>
                <img src={previewUrl} alt="Image preview" className="image-preview" />
                <button type="button" onClick={handleClearImage} className="clear-image-btn">&times;</button>
              </>
            ) : (
              <div className="file-input-content">
                <p>Drag & Drop an image here, or click to select</p>
                <span>PNG, JPG, GIF up to 10MB</span>
              </div>
            )}
          </div>

          <textarea
            value={question}
            onChange={(e: ChangeEvent<HTMLTextAreaElement>) => setQuestion(e.target.value)}
            placeholder="Ask a question about the image..."
            className="question-input"
            rows={3}
          />
          <button type="submit" disabled={loading || !imageFile} className="btn-primary">
            {loading ? 'Analyzing...' : 'Analyze Image'}
          </button>
        </form>

        {loading && <div className="loader">Analyzing your image, please wait...</div>}

        {error && (
          <div className="error-message">
            <strong>Error:</strong> {error}
          </div>
        )}

        {answer && !loading && (
          <div className="answer-card">
            <h3>Analysis Result</h3>
            <p>{answer}</p>
          </div>
        )}
      </main>
    </div>
  );
};

export default App;