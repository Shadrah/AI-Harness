package domain

import (
	"encoding/json"
	"time"
)

type EventKind string

const (
	EventMessage        EventKind = "message"
	EventReasoning      EventKind = "reasoning_summary"
	EventToolCall       EventKind = "tool_call"
	EventToolResult     EventKind = "tool_result"
	EventApproval       EventKind = "approval"
	EventUsage          EventKind = "usage"
	EventStatus         EventKind = "status"
	EventError          EventKind = "error"
	EventProviderNative EventKind = "provider_native"
)

type Provenance struct {
	SourceHarness string    `json:"source_harness"`
	SourceID      string    `json:"source_id,omitempty"`
	SourcePath    string    `json:"source_path,omitempty"`
	ImportedAt    time.Time `json:"imported_at,omitempty"`
	ContentHash   string    `json:"content_hash,omitempty"`
}

type Event struct {
	ID         string          `json:"id"`
	ThreadID   string          `json:"thread_id"`
	Kind       EventKind       `json:"kind"`
	Role       string          `json:"role,omitempty"`
	CreatedAt  time.Time       `json:"created_at"`
	Content    []ContentBlock  `json:"content,omitempty"`
	Provider   string          `json:"provider,omitempty"`
	Model      string          `json:"model,omitempty"`
	ToolName   string          `json:"tool_name,omitempty"`
	ToolCallID string          `json:"tool_call_id,omitempty"`
	Provenance *Provenance     `json:"provenance,omitempty"`
	Native     json.RawMessage `json:"native,omitempty"`
}
