package appserver

type ModelListParams struct {
	Limit int `json:"limit,omitempty"`
}

type ModelListResponse struct {
	Data       []Model `json:"data"`
	NextCursor string  `json:"nextCursor,omitempty"`
}

type Model struct {
	ID                        string                  `json:"id"`
	Model                     string                  `json:"model"`
	DisplayName               string                  `json:"displayName"`
	Description               string                  `json:"description"`
	Hidden                    bool                    `json:"hidden"`
	IsDefault                 bool                    `json:"isDefault"`
	InputModalities           []string                `json:"inputModalities"`
	DefaultReasoningEffort    string                  `json:"defaultReasoningEffort"`
	SupportedReasoningEfforts []ReasoningEffortOption `json:"supportedReasoningEfforts"`
	SupportsPersonality       bool                    `json:"supportsPersonality"`
}

type ReasoningEffortOption struct {
	ReasoningEffort string `json:"reasoningEffort"`
	Description     string `json:"description"`
}

type ThreadStartParams struct {
	CWD            string `json:"cwd,omitempty"`
	Model          string `json:"model,omitempty"`
	ApprovalPolicy string `json:"approvalPolicy,omitempty"`
	Sandbox        string `json:"sandbox,omitempty"`
	Ephemeral      bool   `json:"ephemeral,omitempty"`
}

type ThreadStartResponse struct {
	Thread struct {
		ID string `json:"id"`
	} `json:"thread"`
}

type TurnStartParams struct {
	ThreadID string      `json:"threadId"`
	Input    []UserInput `json:"input"`
	Model    string      `json:"model,omitempty"`
	Effort   string      `json:"effort,omitempty"`
}

type TurnStartResponse struct {
	Turn struct {
		ID string `json:"id"`
	} `json:"turn"`
}

type UserInput struct {
	Type   string `json:"type"`
	Text   string `json:"text,omitempty"`
	URL    string `json:"url,omitempty"`
	Path   string `json:"path,omitempty"`
	Detail string `json:"detail,omitempty"`
}
