using System;
using System.Collections.Generic;
using System.Linq;
using Echo.ControlFlow.Collections;
using Echo.Core.Graphing;

namespace Echo.ControlFlow
{
    /// <summary>
    /// Provides a generic base implementation of a control flow graph that contains for each node a user predefined
    /// object in a type safe manner. 
    /// </summary>
    /// <typeparam name="TInstruction">The type of data that each node in the graph stores.</typeparam>
    public class ControlFlowGraph<TInstruction> : IGraph
    {
        private ControlFlowNode<TInstruction> _entrypoint;

        /// <summary>
        /// Creates a new empty graph.
        /// </summary>
        public ControlFlowGraph()
        {
            Nodes = new NodeCollection<TInstruction>(this);
        }

        /// <summary>
        /// Gets or sets the node that is executed first in the control flow graph.
        /// </summary>
        public ControlFlowNode<TInstruction> Entrypoint
        {
            get => _entrypoint;
            set
            {
                if (_entrypoint != value)
                {
                    if (!Nodes.Contains(value))
                        throw new ArgumentException("Node is not present in the graph.", nameof(value));
                    _entrypoint = value;
                }
            }
        }

        /// <summary>
        /// Gets a collection of all basic blocks present in the graph.
        /// </summary>
        public NodeCollection<TInstruction> Nodes
        {
            get;
        }
        
        /// <summary>
        /// Gets a collection of all edges that transfer control from one block to the other in the graph.
        /// </summary>
        /// <returns>The edges.</returns>
        public IEnumerable<ControlFlowEdge<TInstruction>> GetEdges()
        {
            return Nodes.SelectMany(n => n.GetOutgoingEdges());
        }

        /// <summary>
        /// Searches for a node in the control flow graph with the provided offset or identifier.
        /// </summary>
        /// <param name="offset">The offset of the node to find.</param>
        /// <returns>The node.</returns>
        public ControlFlowNode<TInstruction> GetNodeByOffset(long offset) => Nodes[offset];
        
        INode ISubGraph.GetNodeById(long id) => Nodes[id];

        IEnumerable<INode> ISubGraph.GetNodes() => Nodes;

        IEnumerable<IEdge> IGraph.GetEdges() => GetEdges();

    }
}