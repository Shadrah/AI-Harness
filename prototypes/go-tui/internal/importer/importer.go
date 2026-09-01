package importer

import (
	"context"
	"time"
)

type Kind string

const (
	KindProject      Kind = "project"
	KindInstruction  Kind = "instruction"
	KindConversation Kind = "conversation"
	KindContext      Kind = "context"
	KindMemory       Kind = "memory"
	KindSkill        Kind = "skill"
	KindPlugin       Kind = "plugin"
	KindMCP          Kind = "mcp"
	KindHook         Kind = "hook"
	KindSubagent     Kind = "subagent"
)

type Source struct {
	ImporterID string `json:"importer_id"`
	Harness    string `json:"harness"`
	Path       string `json:"path"`
	Label      string `json:"label"`
}

type Item struct {
	ID        string    `json:"id"`
	Kind      Kind      `json:"kind"`
	Label     string    `json:"label"`
	Path      string    `json:"path,omitempty"`
	Bytes     int64     `json:"bytes,omitempty"`
	Modified  time.Time `json:"modified,omitempty"`
	Sensitive bool      `json:"sensitive,omitempty"`
	Selected  bool      `json:"selected"`
	Reason    string    `json:"reason,omitempty"`
}

type Inventory struct {
	Source Source `json:"source"`
	Items  []Item `json:"items"`
}

type Action string

const (
	ActionReference Action = "reference"
	ActionCopy      Action = "copy"
	ActionSkip      Action = "skip"
)

type PlannedItem struct {
	Item        Item   `json:"item"`
	Action      Action `json:"action"`
	Destination string `json:"destination,omitempty"`
}

type Plan struct {
	Source    Source        `json:"source"`
	CreatedAt time.Time     `json:"created_at"`
	Items     []PlannedItem `json:"items"`
	Warnings  []string      `json:"warnings,omitempty"`
}

type Sink interface {
	Put(ctx context.Context, item PlannedItem) error
}

type Importer interface {
	ID() string
	Detect(ctx context.Context) ([]Source, error)
	Scan(ctx context.Context, source Source) (Inventory, error)
	Preview(ctx context.Context, inventory Inventory) (Plan, error)
	Import(ctx context.Context, plan Plan, sink Sink) error
}
