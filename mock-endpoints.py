#!/usr/bin/env python3
"""
Simple mock HTTP server to simulate AWS and Azure endpoints for testing.
"""

from http.server import HTTPServer, BaseHTTPRequestHandler
import json
import time
import threading
from urllib.parse import urlparse, parse_qs

class MockEndpointHandler(BaseHTTPRequestHandler):
    def __init__(self, *args, **kwargs):
        self.request_count = 0
        super().__init__(*args, **kwargs)
    
    def do_POST(self):
        """Handle POST requests to simulate data saving endpoints."""
        self.request_count += 1
        
        # Parse the URL to determine which endpoint
        parsed_url = urlparse(self.path)
        
        # Read the request body
        content_length = int(self.headers.get('Content-Length', 0))
        body = self.rfile.read(content_length)
        
        try:
            data = json.loads(body.decode('utf-8'))
            print(f"[{self.server.server_name}] Received request #{self.request_count}: {data.get('id', 'unknown')}")
            
            # Simulate some processing time
            time.sleep(0.1)
            
            # Return success response
            response = {
                "success": True,
                "message": f"Data saved successfully to {self.server.server_name}",
                "id": data.get('id'),
                "timestamp": time.time()
            }
            
            self.send_response(200)
            self.send_header('Content-Type', 'application/json')
            self.end_headers()
            self.wfile.write(json.dumps(response).encode('utf-8'))
            
        except Exception as e:
            print(f"[{self.server.server_name}] Error processing request: {e}")
            self.send_response(500)
            self.send_header('Content-Type', 'application/json')
            self.end_headers()
            error_response = {"error": str(e)}
            self.wfile.write(json.dumps(error_response).encode('utf-8'))
    
    def do_GET(self):
        """Handle GET requests for health checks."""
        self.send_response(200)
        self.send_header('Content-Type', 'application/json')
        self.end_headers()
        response = {
            "status": "healthy",
            "service": self.server.server_name,
            "timestamp": time.time()
        }
        self.wfile.write(json.dumps(response).encode('utf-8'))
    
    def log_message(self, format, *args):
        """Override to reduce logging noise."""
        pass

def run_mock_server(port, name):
    """Run a mock server on the specified port."""
    server = HTTPServer(('0.0.0.0', port), MockEndpointHandler)
    server.server_name = name
    print(f"Starting {name} mock server on port {port}")
    server.serve_forever()

if __name__ == "__main__":
    # Start AWS mock server on port 8081
    aws_thread = threading.Thread(target=run_mock_server, args=(8081, "AWS-API"))
    aws_thread.daemon = True
    aws_thread.start()
    
    # Start Azure mock server on port 8082
    azure_thread = threading.Thread(target=run_mock_server, args=(8082, "Azure-API"))
    azure_thread.daemon = True
    azure_thread.start()
    
    print("Mock endpoints started:")
    print("- AWS API: http://localhost:8081")
    print("- Azure API: http://localhost:8082")
    print("Press Ctrl+C to stop")
    
    try:
        # Keep the main thread alive
        while True:
            time.sleep(1)
    except KeyboardInterrupt:
        print("\nShutting down mock servers...") 