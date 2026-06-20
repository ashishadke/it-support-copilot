import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';

// These match the JSON your API returns from /api/chat.
// (ASP.NET sends C# PascalCase as camelCase, so FileName -> fileName, etc.)
export interface Citation {
  fileName: string;
  chunkIndex: number;
  score: number;
}
export interface ChatAnswer {
  answer: string;
  sources: Citation[];
}

@Injectable({ providedIn: 'root' })   // makes this service injectable everywhere
export class ChatService {
  private http = inject(HttpClient);

  // ⚠️ Set this to YOUR running API's HTTP address:
  //   - VS F5 (https profile) also serves http on 5073  -> http://localhost:5073
  //   - `dotnet run --urls http://localhost:5080`       -> http://localhost:5080
  private baseUrl = 'http://localhost:5073';

  // POST { question } to /api/chat, expect a ChatAnswer back.
  ask(question: string): Observable<ChatAnswer> {
    return this.http.post<ChatAnswer>(`${this.baseUrl}/api/chat`, { question });
  }
  // Streaming version: calls /api/chat/stream and yields each token as it arrives.
async *askStream(question: string, history: { role: string; text: string }[] = []): AsyncGenerator<string> {
  const res = await fetch(`${this.baseUrl}/api/chat/stream`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ question, history })   // send recent turns so the agent has context
  });

  if (!res.body) return;
  const reader = res.body.getReader();   // lets us read the response as it streams
  const decoder = new TextDecoder();     // turns raw bytes into text
  let buffer = '';

  while (true) {
    const { value, done } = await reader.read();
    if (done) break;                                   // stream finished

    buffer += decoder.decode(value, { stream: true }); // add new bytes to our buffer
    const events = buffer.split('\n\n');               // SSE events are separated by a blank line
    buffer = events.pop() ?? '';                       // keep the last (maybe incomplete) piece

    for (const evt of events) {
      if (evt.startsWith('data: ')) {
        yield JSON.parse(evt.slice(6)) as string;   // un-escape back to the real token (newlines restored)
      }
    }
  }
}
uploadDocument(file: File): Observable<any> {
  const form = new FormData();
  form.append('file', file);                 // field name MUST be "file"
  return this.http.post(`${this.baseUrl}/api/documents/upload`, form);
}
}