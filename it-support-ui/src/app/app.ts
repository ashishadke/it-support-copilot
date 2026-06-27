import { Component, signal, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ChatService, Citation } from './chat.service';

// One message in the chat transcript.
interface ChatMessage {
  role: 'user' | 'assistant';
  text: string;
  sources?: Citation[];
}

// Reuse the same conversationId across page reloads (stored in the browser) so the
// server-side memory persists through a refresh. A "new chat" would clear this key.
function getConversationId(): string {
  const key = 'itsupport.conversationId';
  let id = localStorage.getItem(key);
  if (!id) {
    id = crypto.randomUUID();
    localStorage.setItem(key, id);
  }
  return id;
}

@Component({
  selector: 'app-root',
  imports: [FormsModule],          // FormsModule gives us [(ngModel)] for the input box
  templateUrl: './app.html',
  styleUrl: './app.css'
})
export class App {
  private chat = inject(ChatService);

  messages = signal<ChatMessage[]>([]);  // the whole conversation (reactive)
  loading = signal(false);               // true while waiting for the API
  input = '';                            // bound to the text box
  conversationId = getConversationId();  // persisted in the browser so memory survives refresh

 async send() {
  const question = this.input.trim();
  if (!question || this.loading()) return;

  this.messages.update(list => [...list, { role: 'user', text: question }]);
  this.input = '';
  this.loading.set(true);

  let started = false;
  try {
    for await (const token of this.chat.askStream(question, this.conversationId)) {
      if (!started) {
        // first token arrived: stop "Thinking…" and add an empty assistant bubble to fill
        started = true;
        this.loading.set(false);
        this.messages.update(list => [...list, { role: 'assistant', text: '' }]);
      }
      // append this token to the last (assistant) message
      this.messages.update(list => {
        const copy = [...list];
        const last = copy[copy.length - 1];
        copy[copy.length - 1] = { ...last, text: last.text + token };
        return copy;
      });
    }
  } catch {
    this.messages.update(list => [...list, { role: 'assistant', text: '⚠️ Could not reach the API.' }]);
  } finally {
    this.loading.set(false);
  }
}
uploadStatus = signal('');
onFileSelected(event: Event) {
  const input = event.target as HTMLInputElement;
  const file = input.files?.[0];
  if (!file) return;

  this.uploadStatus.set(`Uploading ${file.name}…`);
  this.chat.uploadDocument(file).subscribe({
    next: (res: any) => {
      this.uploadStatus.set(`✅ Uploaded ${res.file} (${res.chunksStored} chunks)`);
      input.value = '';   // reset so the same file can be picked again
    },
    error: () => this.uploadStatus.set('❌ Upload failed')
  });
}
}