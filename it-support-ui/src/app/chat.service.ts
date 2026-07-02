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
// One row in the "recent chats" sidebar (from GET /api/conversations).
export interface ConversationSummary {
  id: string;
  title: string;
  updatedAt: string;
}
// One stored message when restoring a chat (from GET /api/conversations/{id}).
export interface StoredMessage {
  role: string;
  text: string;
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
async *askStream(question: string, conversationId: string): AsyncGenerator<string> {
  const res = await fetch(`${this.baseUrl}/api/chat/stream`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ question, conversationId })   // server loads prior turns by conversationId
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
// The 10 most recent chats for the sidebar.
getConversations(): Observable<ConversationSummary[]> {
  return this.http.get<ConversationSummary[]>(`${this.baseUrl}/api/conversations`);
}
// Full transcript of one chat (for restoring it when clicked).
getMessages(conversationId: string): Observable<StoredMessage[]> {
  return this.http.get<StoredMessage[]>(`${this.baseUrl}/api/conversations/${conversationId}`);
}
uploadDocument(file: File): Observable<any> {
  const form = new FormData();
  form.append('file', file);                 // field name MUST be "file"
  return this.http.post(`${this.baseUrl}/api/documents/upload`, form);
}
}