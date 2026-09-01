package appserver

import (
	"bufio"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os/exec"
	"sync"
)

type rpcRequest struct {
	JSONRPC string `json:"jsonrpc"`
	ID      int64  `json:"id,omitempty"`
	Method  string `json:"method"`
	Params  any    `json:"params,omitempty"`
}

type rpcError struct {
	Code    int             `json:"code"`
	Message string          `json:"message"`
	Data    json.RawMessage `json:"data,omitempty"`
}

type rpcMessage struct {
	JSONRPC string          `json:"jsonrpc"`
	ID      json.RawMessage `json:"id,omitempty"`
	Method  string          `json:"method,omitempty"`
	Params  json.RawMessage `json:"params,omitempty"`
	Result  json.RawMessage `json:"result,omitempty"`
	Error   *rpcError       `json:"error,omitempty"`
}

type Notification struct {
	Method string
	Params json.RawMessage
	ID     json.RawMessage
	Raw    json.RawMessage
}

type response struct {
	result json.RawMessage
	err    error
}

type Client struct {
	command       *exec.Cmd
	stdin         io.WriteCloser
	notifications chan Notification
	done          chan struct{}

	writeMu sync.Mutex
	mu      sync.Mutex
	nextID  int64
	pending map[int64]chan response
	err     error
}

func Start(ctx context.Context, executable string, stderr io.Writer) (*Client, error) {
	if executable == "" {
		executable = "codex"
	}
	command := exec.CommandContext(ctx, executable, "app-server", "--stdio")
	stdout, err := command.StdoutPipe()
	if err != nil {
		return nil, err
	}
	stdin, err := command.StdinPipe()
	if err != nil {
		return nil, err
	}
	command.Stderr = stderr
	if err := command.Start(); err != nil {
		return nil, err
	}
	client := &Client{
		command:       command,
		stdin:         stdin,
		notifications: make(chan Notification, 128),
		done:          make(chan struct{}),
		pending:       make(map[int64]chan response),
	}
	go client.read(stdout)
	return client, nil
}

func (c *Client) Initialize(ctx context.Context) error {
	params := map[string]any{
		"clientInfo": map[string]any{
			"name":    "harness",
			"title":   "Harness",
			"version": "0.1.0",
		},
		"capabilities": map[string]any{"experimentalApi": true},
	}
	var result json.RawMessage
	if err := c.Call(ctx, "initialize", params, &result); err != nil {
		return err
	}
	return c.Notify("initialized", map[string]any{})
}

func (c *Client) Models(ctx context.Context) (ModelListResponse, error) {
	var response ModelListResponse
	err := c.Call(ctx, "model/list", ModelListParams{Limit: 100}, &response)
	return response, err
}

func (c *Client) StartThread(ctx context.Context, params ThreadStartParams) (ThreadStartResponse, error) {
	var response ThreadStartResponse
	err := c.Call(ctx, "thread/start", params, &response)
	return response, err
}

// StartTurn submits typed text and image inputs. Turn progress, tool activity,
// approval requests, and completion continue through Notifications().
func (c *Client) StartTurn(ctx context.Context, params TurnStartParams) (TurnStartResponse, error) {
	var response TurnStartResponse
	err := c.Call(ctx, "turn/start", params, &response)
	return response, err
}

func (c *Client) Call(ctx context.Context, method string, params, target any) error {
	c.mu.Lock()
	c.nextID++
	id := c.nextID
	responses := make(chan response, 1)
	c.pending[id] = responses
	c.mu.Unlock()

	if err := c.write(rpcRequest{JSONRPC: "2.0", ID: id, Method: method, Params: params}); err != nil {
		c.removePending(id)
		return err
	}

	select {
	case <-ctx.Done():
		c.removePending(id)
		return ctx.Err()
	case <-c.done:
		return c.Err()
	case response := <-responses:
		if response.err != nil {
			return response.err
		}
		if target == nil || len(response.result) == 0 {
			return nil
		}
		return json.Unmarshal(response.result, target)
	}
}

func (c *Client) Notify(method string, params any) error {
	return c.write(rpcRequest{JSONRPC: "2.0", Method: method, Params: params})
}

func (c *Client) Notifications() <-chan Notification { return c.notifications }

func (c *Client) Close() error {
	_ = c.stdin.Close()
	if c.command.Process != nil {
		_ = c.command.Process.Kill()
	}
	<-c.done
	return nil
}

func (c *Client) Err() error {
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.err == nil {
		return errors.New("Codex app-server stopped")
	}
	return c.err
}

func (c *Client) write(value any) error {
	data, err := json.Marshal(value)
	if err != nil {
		return err
	}
	c.writeMu.Lock()
	defer c.writeMu.Unlock()
	data = append(data, '\n')
	_, err = c.stdin.Write(data)
	return err
}

func (c *Client) read(reader io.Reader) {
	defer close(c.done)
	defer close(c.notifications)
	scanner := bufio.NewScanner(reader)
	scanner.Buffer(make([]byte, 64*1024), 16*1024*1024)
	for scanner.Scan() {
		raw := append(json.RawMessage(nil), scanner.Bytes()...)
		var message rpcMessage
		if err := json.Unmarshal(raw, &message); err != nil {
			continue
		}
		if message.Method != "" {
			c.notifications <- Notification{Method: message.Method, Params: message.Params, ID: message.ID, Raw: raw}
			continue
		}
		var id int64
		if err := json.Unmarshal(message.ID, &id); err != nil {
			continue
		}
		c.mu.Lock()
		responses := c.pending[id]
		delete(c.pending, id)
		c.mu.Unlock()
		if responses == nil {
			continue
		}
		if message.Error != nil {
			responses <- response{err: fmt.Errorf("Codex RPC %d: %s", message.Error.Code, message.Error.Message)}
		} else {
			responses <- response{result: message.Result}
		}
	}
	c.finish(scanner.Err())
}

func (c *Client) finish(err error) {
	c.mu.Lock()
	if err == nil {
		err = c.command.Wait()
	}
	c.err = err
	for id, responses := range c.pending {
		responses <- response{err: c.err}
		delete(c.pending, id)
	}
	c.mu.Unlock()
}

func (c *Client) removePending(id int64) {
	c.mu.Lock()
	delete(c.pending, id)
	c.mu.Unlock()
}
